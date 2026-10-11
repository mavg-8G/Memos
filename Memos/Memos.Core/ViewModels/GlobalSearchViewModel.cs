using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Memos.Core.Helpers;
using Memos.Core.Models;
using Memos.Core.Services;
using System.Collections.ObjectModel;

namespace Memos.Core.ViewModels;

/// <summary>One match found in an open tab, with the line it is on for display.</summary>
public sealed record GlobalSearchResult(DocumentViewModel Document, int Line, int Start, int Length, string Preview)
{
    public string DocumentName => Document.DisplayName;

    /// <summary>"notes.txt : 12" — where the match is.</summary>
    public string Location => $"{Document.DisplayName} : {Line}";

    /// <summary>Also what assistive technology reads for the list item.</summary>
    public override string ToString() => Location;
}

/// <summary>Find and replace across every open tab.</summary>
public partial class GlobalSearchViewModel : ObservableObject
{
    /// <summary>The list shows at most this many matches; the summary still counts all of them.</summary>
    public const int MaxListedResults = 2000;

    private const int PreviewLength = 160;

    private readonly Func<IReadOnlyList<DocumentViewModel>> _documents;
    private readonly Action<DocumentViewModel> _activate;
    private readonly ISearchService _search;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _loc;

    public GlobalSearchViewModel(
        Func<IReadOnlyList<DocumentViewModel>> documents, Action<DocumentViewModel> activate,
        ISearchService search, IDialogService dialogs, ILocalizer localizer)
    {
        _documents = documents;
        _activate = activate;
        _search = search;
        _dialogs = dialogs;
        _loc = localizer;
    }

    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string Replacement { get; set; } = "";
    [ObservableProperty] public partial bool MatchCase { get; set; }
    [ObservableProperty] public partial bool WholeWord { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "";
    [ObservableProperty] public partial bool HasError { get; private set; }

    public ObservableCollection<GlobalSearchResult> Results { get; } = [];

    /// <summary>Raised when the query box should take keyboard focus.</summary>
    public event EventHandler? FocusRequested;

    private SearchOptions Options => new(MatchCase, WholeWord, UseRegex);

    public void Show(string? seed = null)
    {
        if (seed is { Length: > 0 and <= 200 } && !seed.Contains('\n'))
        {
            Query = seed;
        }

        IsVisible = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close()
    {
        IsVisible = false;
        Results.Clear();
        Summary = "";
        HasError = false;
    }

    /// <summary>Searches every open tab and lists the matches in tab order.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        Results.Clear();
        HasError = false;
        if (Query.Length == 0)
        {
            Summary = "";
            return;
        }

        var (query, options) = (Query, Options);
        var documents = _documents().Select(d => (Document: d, d.Text)).ToList();
        List<(DocumentViewModel Document, string Text, IReadOnlyList<TextMatch> Matches)> found;
        try
        {
            found = await Task.Run(() => documents
                .Select(d => (d.Document, d.Text, _search.FindAll(d.Text, query, options)))
                .Where(d => d.Item3.Count > 0)
                .ToList());
        }
        catch (SearchQueryException ex)
        {
            HasError = true;
            Summary = _loc.Get(ex.TimedOut ? "Find_Timeout" : "Find_InvalidRegex");
            return;
        }

        var total = 0;
        foreach (var (document, text, matches) in found)
        {
            total += matches.Count;
            var lines = new LineIndex(text);
            foreach (var match in matches)
            {
                if (Results.Count >= MaxListedResults)
                {
                    break;
                }

                var line = lines.GetLineFromOffset(match.Start);
                Results.Add(new GlobalSearchResult(document, line + 1, match.Start, match.Length,
                    BuildPreview(text, lines.GetLineStart(line), lines.GetLineEnd(line), match.Start)));
            }
        }

        Summary = total == 0
            ? _loc.Get("Find_NoResults")
            : total > Results.Count
                ? _loc.Format("GlobalSearch_SummaryTruncated", total, found.Count, Results.Count)
                : _loc.Format("GlobalSearch_Summary", total, found.Count);
    }

    /// <summary>Replaces every match in every tab after confirmation; one undo step per document.</summary>
    [RelayCommand]
    private async Task ReplaceAllAsync()
    {
        if (Query.Length == 0)
        {
            return;
        }

        var (query, replacement, options) = (Query, Replacement, Options);
        var documents = _documents().Select(d => (Document: d, d.Text)).ToList();
        List<(DocumentViewModel Document, string OldText, string NewText, int Count)> changes;
        try
        {
            changes = await Task.Run(() => documents
                .Select(d =>
                {
                    var (newText, count) = _search.ReplaceAll(d.Text, query, replacement, options);
                    return (d.Document, d.Text, newText, count);
                })
                .Where(c => c.count > 0)
                .ToList());
        }
        catch (SearchQueryException ex)
        {
            HasError = true;
            Summary = _loc.Get(ex.TimedOut ? "Find_Timeout" : "Find_InvalidRegex");
            return;
        }

        HasError = false;
        var total = changes.Sum(c => c.Count);
        if (total == 0)
        {
            Results.Clear();
            Summary = _loc.Get("Find_NoResults");
            return;
        }

        if (!await _dialogs.ConfirmAsync(_loc.Get("GlobalSearch_ConfirmTitle"),
                _loc.Format("GlobalSearch_Confirm", total, changes.Count), _loc.Get("Find_ReplaceAll"), _loc.Get("Btn_Cancel")))
        {
            return;
        }

        var replaced = 0;
        var touched = 0;
        foreach (var (document, oldText, newText, count) in changes)
        {
            // Skip a document that was edited while the confirmation was open.
            if (!ReferenceEquals(document.Text, oldText))
            {
                continue;
            }

            document.ApplyEdit(new TextEdit(0, oldText.Length, newText, Math.Min(document.SelectionStart, newText.Length), 0));
            replaced += count;
            touched++;
        }

        Results.Clear();
        Summary = _loc.Format("GlobalSearch_Replaced", replaced, touched);
    }

    /// <summary>Brings the result's tab to the front and selects the match.</summary>
    public void Open(GlobalSearchResult result)
    {
        if (!_documents().Contains(result.Document))
        {
            return;
        }

        _activate(result.Document);
        result.Document.Select(result.Start, result.Length);
        result.Document.Editor?.Focus();
    }

    /// <summary>The line of the match, trimmed around it when the line is long.</summary>
    private static string BuildPreview(string text, int lineStart, int lineEnd, int matchStart)
    {
        var start = lineStart;
        var prefix = "";
        if (matchStart - lineStart > PreviewLength / 2)
        {
            start = matchStart - PreviewLength / 2;
            if (char.IsLowSurrogate(text[start]))
            {
                start++;
            }

            prefix = "…";
        }

        var end = Math.Min(lineEnd, start + PreviewLength);
        if (end < lineEnd && end > start && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return prefix + text[start..end].Trim() + (end < lineEnd ? "…" : "");
    }
}
