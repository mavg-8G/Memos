using CommunityToolkit.Mvvm.ComponentModel;
using Memos.Core.Helpers;
using Memos.Core.Models;
using Memos.Core.Services;

namespace Memos.Core.ViewModels;

/// <summary>Find and replace state for one document. Searches run off the UI thread on large texts.</summary>
public partial class FindReplaceViewModel : ObservableObject, IDisposable
{
    /// <summary>Matches beyond this number are not collected; the counter then shows it as a lower bound.</summary>
    public const int MaxMatches = 20_000;

    private const int BackgroundThreshold = 100_000;

    private readonly DocumentViewModel _document;
    private readonly ISearchService _search;
    private readonly ILocalizer _localizer;
    private readonly Debouncer _debouncer = new();

    public FindReplaceViewModel(DocumentViewModel document, ISearchService search, ILocalizer localizer)
    {
        _document = document;
        _search = search;
        _localizer = localizer;
    }

    [ObservableProperty] public partial bool IsVisible { get; private set; }
    [ObservableProperty] public partial bool IsReplaceVisible { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string Replacement { get; set; } = "";
    [ObservableProperty] public partial bool MatchCase { get; set; }
    [ObservableProperty] public partial bool WholeWord { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial bool HasError { get; private set; }

    public IReadOnlyList<TextMatch> Matches { get; private set; } = [];

    public int CurrentIndex { get; private set; } = -1;

    /// <summary>Raised when the set of matches or the current one changes, so the editor can redraw highlights.</summary>
    public event EventHandler? MatchesChanged;

    /// <summary>Raised when the search box should take keyboard focus.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Completes when the search triggered by the latest change has finished.</summary>
    public Task Pending => _debouncer.Pending;

    private SearchOptions Options => new(MatchCase, WholeWord, UseRegex);

    public void Show(bool replace)
    {
        // Seed the box with the selection when it is a short, single-line piece of text.
        var selected = _document.SelectedText;
        if (selected.Length is > 0 and <= 200 && !selected.Contains('\n'))
        {
            Query = selected;
        }

        IsReplaceVisible = replace;
        IsVisible = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
        Schedule(selectNearest: false, TimeSpan.Zero);
    }

    public void Close()
    {
        _debouncer.Cancel();
        IsVisible = false;
        SetMatches([], -1);
        StatusText = "";
        HasError = false;
        _document.Editor?.Focus();
    }

    public void FindNext() => Move(forward: true);

    public void FindPrevious() => Move(forward: false);

    public async Task ReplaceCurrentAsync()
    {
        await RefreshAsync(selectNearest: false, CancellationToken.None);
        var index = IndexOfSelection();
        if (index < 0)
        {
            Move(forward: true);
            return;
        }

        try
        {
            _document.ApplyEdit(_search.ReplaceOne(_document.Text, Matches[index], Query, Replacement, Options));
        }
        catch (SearchQueryException ex)
        {
            ShowError(ex);
            return;
        }

        _debouncer.Cancel();
        await RefreshAsync(selectNearest: true, CancellationToken.None);
    }

    public async Task ReplaceAllAsync()
    {
        if (Query.Length == 0)
        {
            return;
        }

        _debouncer.Cancel();
        var text = _document.Text;
        var (query, replacement, options) = (Query, Replacement, Options);
        try
        {
            var (newText, count) = text.Length > BackgroundThreshold
                ? await Task.Run(() => _search.ReplaceAll(text, query, replacement, options))
                : _search.ReplaceAll(text, query, replacement, options);

            if (count > 0 && ReferenceEquals(text, _document.Text))
            {
                var caret = Math.Min(_document.SelectionStart, newText.Length);
                _document.ApplyEdit(new TextEdit(0, text.Length, newText, caret, 0));
            }

            _debouncer.Cancel();
            await RefreshAsync(selectNearest: false, CancellationToken.None);
            StatusText = _localizer.Format("Find_Replaced", count);
        }
        catch (SearchQueryException ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>The document changed under the search: results are recomputed without moving the caret.</summary>
    public void OnDocumentTextChanged()
    {
        if (IsVisible)
        {
            Schedule(selectNearest: false, TimeSpan.FromMilliseconds(150));
        }
    }

    public void Dispose()
    {
        _debouncer.Dispose();
        GC.SuppressFinalize(this);
    }

    partial void OnQueryChanged(string value) => OnCriteriaChanged();

    partial void OnMatchCaseChanged(bool value) => OnCriteriaChanged();

    partial void OnWholeWordChanged(bool value) => OnCriteriaChanged();

    partial void OnUseRegexChanged(bool value) => OnCriteriaChanged();

    private void OnCriteriaChanged()
    {
        if (IsVisible)
        {
            Schedule(selectNearest: true, TimeSpan.FromMilliseconds(120));
        }
    }

    private void Schedule(bool selectNearest, TimeSpan delay) =>
        _debouncer.Run(delay, token => RefreshAsync(selectNearest, token));

    private async Task RefreshAsync(bool selectNearest, CancellationToken token)
    {
        var text = _document.Text;
        var (query, options) = (Query, Options);
        IReadOnlyList<TextMatch> matches;
        try
        {
            matches = text.Length > BackgroundThreshold
                ? await Task.Run(() => _search.FindAll(text, query, options, token, MaxMatches), token)
                : _search.FindAll(text, query, options, token, MaxMatches);
        }
        catch (SearchQueryException ex)
        {
            ShowError(ex);
            return;
        }

        if (token.IsCancellationRequested || !ReferenceEquals(text, _document.Text))
        {
            return;
        }

        HasError = false;
        var index = -1;
        if (selectNearest && matches.Count > 0)
        {
            // Incremental search: jump to the first match at or after the caret, wrapping around.
            index = FirstAtOrAfter(matches, _document.SelectionStart);
            if (index < 0)
            {
                index = 0;
            }

            Matches = matches;
            _document.Select(matches[index].Start, matches[index].Length);
        }

        Matches = matches;
        SetMatches(matches, index >= 0 ? index : IndexOfSelection());
    }

    private void Move(bool forward)
    {
        if (Matches.Count == 0)
        {
            return;
        }

        int index;
        if (forward)
        {
            var from = _document.SelectionStart + (IndexOfSelection() >= 0 ? Math.Max(1, _document.SelectionLength) : 0);
            index = FirstAtOrAfter(Matches, from);
            if (index < 0)
            {
                index = 0;
            }
        }
        else
        {
            index = FirstAtOrAfter(Matches, _document.SelectionStart);
            index = (index < 0 ? Matches.Count : index) - 1;
            if (index < 0)
            {
                index = Matches.Count - 1;
            }
        }

        _document.Select(Matches[index].Start, Matches[index].Length);
        SetMatches(Matches, index);
    }

    private void SetMatches(IReadOnlyList<TextMatch> matches, int index)
    {
        Matches = matches;
        CurrentIndex = index;
        if (IsVisible && !HasError)
        {
            var total = matches.Count >= MaxMatches ? $"{matches.Count}+" : matches.Count.ToString();
            StatusText = Query.Length == 0 ? ""
                : matches.Count == 0 ? _localizer.Get("Find_NoResults")
                : index >= 0 ? _localizer.Format("Find_Count", index + 1, total)
                : _localizer.Format("Find_Total", total);
        }

        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowError(SearchQueryException ex)
    {
        HasError = true;
        StatusText = _localizer.Get(ex.TimedOut ? "Find_Timeout" : "Find_InvalidRegex");
        Matches = [];
        CurrentIndex = -1;
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Index of the match that is exactly the current selection, or -1.</summary>
    private int IndexOfSelection()
    {
        var index = FirstAtOrAfter(Matches, _document.SelectionStart);
        return index >= 0 && Matches[index].Start == _document.SelectionStart && Matches[index].Length == _document.SelectionLength
            ? index
            : -1;
    }

    private static int FirstAtOrAfter(IReadOnlyList<TextMatch> matches, int offset)
    {
        int low = 0, high = matches.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (matches[mid].Start < offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low < matches.Count ? low : -1;
    }
}
