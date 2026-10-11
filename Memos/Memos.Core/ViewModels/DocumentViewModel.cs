using CommunityToolkit.Mvvm.ComponentModel;
using Memos.Core.Helpers;
using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;

namespace Memos.Core.ViewModels;

/// <summary>Services every document needs; grouped so creating a document stays simple.</summary>
public sealed record DocumentServices(ISettingsService Settings, ISearchService Search, IMarkdownService Markdown, ILocalizer Localizer);

public sealed class AnalysisChangedEventArgs(MarkdownAnalysis? analysis, MarkdownAnalysis? previous, (int Start, int End)? dirtyRange) : EventArgs
{
    public MarkdownAnalysis? Analysis { get; } = analysis;

    /// <summary>The analysis this one replaces; the dirty range is only valid relative to it.</summary>
    public MarkdownAnalysis? Previous { get; } = previous;

    /// <summary>Part of the text whose editor styling must be redone, or null to redo all of it.</summary>
    public (int Start, int End)? DirtyRange { get; } = dirtyRange;
}

/// <summary>
/// State of one open document: text, file identity, undo history, caret, statistics and, for
/// Markdown, the latest analysis. It works with or without an editing control attached.
/// </summary>
public partial class DocumentViewModel : ObservableObject, IDisposable
{
    /// <summary>Virtual host the preview uses to load images from the document's folder.</summary>
    public const string PreviewHost = "memos-doc.example";

    public const string PreviewOrigin = "https://" + PreviewHost;

    private readonly DocumentServices _services;
    private readonly UndoHistory _history = new();
    private readonly Debouncer _analysisDebouncer = new();
    private readonly Debouncer _statsDebouncer = new();
    private LineIndex? _lineIndex;
    private TextChange? _pendingChange;
    private bool _navigatingHistory;
    private bool _applyingCommand;
    private bool _metadataModified;
    private string _richText = "";
    private string _savedRichText = "";

    public DocumentViewModel(DocumentServices services, DocumentKind kind, string displayName)
    {
        _services = services;
        var settings = services.Settings.Current;
        Kind = kind;
        DisplayName = displayName;
        Encoding = settings.DefaultEncoding;
        LineEnding = settings.DefaultLineEnding;
        ZoomPercent = settings.DefaultZoom;
        ViewMode = settings.MarkdownViewMode;
        Find = new FindReplaceViewModel(this, services.Search, services.Localizer);
    }

    /// <summary>Quiet time before Markdown is re-analysed after an edit.</summary>
    public static TimeSpan AnalysisDelay { get; set; } = TimeSpan.FromMilliseconds(180);

    public Guid Id { get; init; } = Guid.NewGuid();

    public FindReplaceViewModel Find { get; }

    /// <summary>Current text with '\n' line separators.</summary>
    public string Text { get; private set; } = "";

    public string RichText => _richText;

    /// <summary>Increases with every text change.</summary>
    public int TextVersion { get; private set; }

    public int SelectionStart { get; private set; }

    public int SelectionLength { get; private set; }

    public FileStamp? Stamp { get; set; }

    /// <summary>Disk state the user has already been told about, so the same change is not announced twice.</summary>
    public FileStamp? NotifiedStamp { get; set; }

    /// <summary>Text version last written to the session backup.</summary>
    public int BackupVersion { get; set; } = -1;

    public MarkdownAnalysis? Analysis { get; private set; }

    public IEditorSurface? Editor { get; set; }

    public int LineCount => Lines.LineCount;

    public bool IsMarkdown => Kind == DocumentKind.Markdown;

    public bool IsRichText => Kind == DocumentKind.RichText;

    /// <summary>A new, empty, never-edited document; opening a file may take its place.</summary>
    public bool IsPristine => FilePath is null && !IsModified && Text.Length == 0;

    /// <summary>Base URL that makes relative image paths resolve inside the preview.</summary>
    public string? PreviewResourceBase => FilePath is null ? null : PreviewOrigin + "/";

    public string? Directory => FilePath is null ? null : Path.GetDirectoryName(FilePath);

    [ObservableProperty] public partial string? FilePath { get; set; }
    [ObservableProperty] public partial string DisplayName { get; set; }
    [ObservableProperty] public partial DocumentKind Kind { get; set; }
    [ObservableProperty] public partial bool IsModified { get; private set; }
    [ObservableProperty] public partial TextEncodingId Encoding { get; private set; }
    [ObservableProperty] public partial LineEnding LineEnding { get; private set; }
    [ObservableProperty] public partial int Line { get; private set; } = 1;
    [ObservableProperty] public partial int Column { get; private set; } = 1;
    [ObservableProperty] public partial int CharacterCount { get; private set; }
    [ObservableProperty] public partial int WordCount { get; private set; }
    [ObservableProperty] public partial int ReadingMinutes { get; private set; }
    [ObservableProperty] public partial int ZoomPercent { get; set; }
    [ObservableProperty] public partial MarkdownViewMode ViewMode { get; set; }
    [ObservableProperty] public partial bool IsOutlineVisible { get; set; }
    [ObservableProperty] public partial DocumentNotice? Notice { get; set; }
    [ObservableProperty] public partial IReadOnlyList<HeadingInfo> Headings { get; private set; } = [];
    [ObservableProperty] public partial bool CanUndo { get; private set; }
    [ObservableProperty] public partial bool CanRedo { get; private set; }

    /// <summary>Overwrite mode: typed characters replace the ones under the caret (Insert key).</summary>
    [ObservableProperty] public partial bool IsOverwrite { get; set; }

    /// <summary>Raised after the text changes, whatever the origin.</summary>
    public event EventHandler? TextChanged;

    public event EventHandler<AnalysisChangedEventArgs>? AnalysisChanged;

    private LineIndex Lines => _lineIndex ??= new LineIndex(Text);

    private AppSettings Settings => _services.Settings.Current;

    // ---- Content ----------------------------------------------------------------------------

    /// <summary>Replaces the whole content with what was read from disk and forgets the undo history.</summary>
    public void LoadFrom(LoadedText loaded, string path)
    {
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        Encoding = loaded.Encoding;
        LineEnding = loaded.LineEnding ?? Settings.DefaultLineEnding;
        Stamp = loaded.Stamp;
        NotifiedStamp = null;
        ResetText(loaded.Text);
    }

    public void LoadRichText(LoadedRichText loaded, string path)
    {
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        Stamp = loaded.Stamp;
        NotifiedStamp = null;
        RestoreRichText(loaded.Rtf, modified: false);
    }

    public void RestoreRichText(string rtf, bool modified)
    {
        _richText = rtf;
        _savedRichText = modified ? "" : rtf;
        if (Editor is IRichEditorSurface rich)
        {
            rich.LoadRichText(rtf);
        }
        else
        {
            // Without a control the text is read from the RTF, so search and statistics work on
            // tabs that have not been shown yet. The control replaces it with its own reading.
            _navigatingHistory = true;
            try { CommitText(RichTextCodec.ToPlainText(rtf)); }
            finally { _navigatingHistory = false; }
        }

        UpdateHistoryState();
    }

    public void ConvertToRichText()
    {
        if (IsRichText) return;

        // An untouched blank tab stays untouched; anything else has to be saved under a new .rtf name.
        var pristine = IsPristine;
        _richText = RichTextCodec.FromPlainText(Text);
        _savedRichText = pristine ? _richText : "";
        FilePath = null;
        Stamp = null;
        DisplayName = Path.GetFileNameWithoutExtension(DisplayName);
        Kind = DocumentKind.RichText;
        UpdateHistoryState();
    }

    /// <summary>
    /// The control has loaded the document. It writes RTF its own way, so its version becomes the
    /// reference: otherwise a file would look modified as soon as it is opened.
    /// </summary>
    public void OnEditorRichTextLoaded(string text, string rtf)
    {
        var unmodified = string.Equals(_richText, _savedRichText, StringComparison.Ordinal);
        _richText = rtf;
        if (unmodified) _savedRichText = rtf;
        _navigatingHistory = true;
        try { CommitText(text); }
        finally { _navigatingHistory = false; }
        UpdateHistoryState();
    }

    /// <summary>Sets the content without creating an undo step. <paramref name="modified"/> marks it as unsaved.</summary>
    public void ResetText(string text, bool modified = false)
    {
        _navigatingHistory = true;
        try
        {
            if (Editor is not null)
            {
                Editor.ApplyEdit(new TextEdit(0, Text.Length, text, 0, 0));
            }
            else
            {
                CommitText(text);
                SetSelection(0, 0);
            }
        }
        finally
        {
            _navigatingHistory = false;
        }

        _history.Clear();
        if (modified)
        {
            _history.MarkUnsaved();
        }

        _metadataModified = false;
        _pendingChange = null;
        UpdateHistoryState();

        // The editor's styling no longer corresponds to any previous analysis: start over.
        _ = RefreshAnalysisAsync();
    }

    public void MarkSaved(string path, FileStamp stamp)
    {
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        Stamp = stamp;
        NotifiedStamp = null;
        _history.MarkSaved();
        if (IsRichText) _savedRichText = _richText;
        _metadataModified = false;
        UpdateHistoryState();
    }

    /// <summary>Changes how the document will be written. The text itself is untouched.</summary>
    public void SetEncoding(TextEncodingId encoding)
    {
        if (Encoding != encoding)
        {
            Encoding = encoding;
            _metadataModified = true;
            UpdateHistoryState();
        }
    }

    public void SetLineEnding(LineEnding lineEnding)
    {
        if (LineEnding != lineEnding)
        {
            LineEnding = lineEnding;
            _metadataModified = true;
            UpdateHistoryState();
        }
    }

    // ---- Called by the editing control ------------------------------------------------------------

    public void OnEditorTextChanged(string newText) => CommitText(newText);

    public void OnEditorRichTextChanged(string newText, string rtf)
    {
        if (string.Equals(_richText, rtf, StringComparison.Ordinal) && string.Equals(Text, newText, StringComparison.Ordinal)) return;
        _richText = rtf;
        var previousVersion = TextVersion;
        _navigatingHistory = true;
        try { CommitText(newText); }
        finally { _navigatingHistory = false; }
        if (TextVersion == previousVersion)
        {
            TextVersion++;
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
        UpdateHistoryState();
    }

    public void ToggleRichStyle(RichStyle style) => (Editor as IRichEditorSurface)?.ToggleRichStyle(style);

    public void OnEditorSelectionChanged(int start, int length) => SetSelection(start, length);

    // ---- Editing ----------------------------------------------------------------------------

    /// <summary>Applies an edit as one undoable step. A null edit is a no-op.</summary>
    public void ApplyEdit(TextEdit? edit)
    {
        if (edit is not { } value)
        {
            return;
        }

        var minimal = TextOperations.Minimize(Text, value);
        _applyingCommand = true;
        try
        {
            if (Editor is not null)
            {
                Editor.ApplyEdit(minimal);
            }
            else
            {
                CommitText(minimal.Apply(Text));
                SetSelection(minimal.SelectionStart, minimal.SelectionLength);
            }
        }
        finally
        {
            _applyingCommand = false;
        }
    }

    public void Undo()
    {
        if (IsRichText)
        {
            (Editor as IRichEditorSurface)?.UndoRich();
            UpdateHistoryState();
        }
        else Navigate(_history.Undo());
    }

    public void Redo()
    {
        if (IsRichText)
        {
            (Editor as IRichEditorSurface)?.RedoRich();
            UpdateHistoryState();
        }
        else Navigate(_history.Redo());
    }

    public void Select(int start, int length)
    {
        start = Math.Clamp(start, 0, Text.Length);
        length = Math.Clamp(length, 0, Text.Length - start);
        if (Editor is not null)
        {
            Editor.Select(start, length);
        }

        SetSelection(start, length);
    }

    /// <summary>Moves the caret to the start of a one-based line.</summary>
    public void GoToLine(int line) => Select(Lines.GetLineStart(line - 1), 0);

    public int GetLineStart(int zeroBasedLine) => Lines.GetLineStart(zeroBasedLine);

    public int GetLineFromOffset(int offset) => Lines.GetLineFromOffset(offset);

    public void DuplicateLines() => ApplyEdit(TextOperations.DuplicateLines(Text, SelectionStart, SelectionLength));

    public void DeleteLines() => ApplyEdit(TextOperations.DeleteLines(Text, SelectionStart, SelectionLength));

    public void MoveLines(bool up) => ApplyEdit(TextOperations.MoveLines(Text, SelectionStart, SelectionLength, up));

    public void ChangeCase(bool upper) => ApplyEdit(TextOperations.ChangeCase(Text, SelectionStart, SelectionLength, upper));

    public void TrimTrailingWhitespace() => ApplyEdit(TextOperations.TrimTrailingWhitespace(Text, SelectionStart, SelectionLength));

    public void Indent() => ApplyEdit(TextOperations.Indent(Text, SelectionStart, SelectionLength, IndentUnit));

    public void Outdent() => ApplyEdit(TextOperations.Outdent(Text, SelectionStart, SelectionLength, Settings.TabSize));

    public void ConvertIndentation(bool toSpaces) => ApplyEdit(TextOperations.ConvertIndentation(Text, toSpaces, Settings.TabSize));

    /// <summary>Tab key: indents a multi-line selection, otherwise inserts a tab or spaces up to the next tab stop.</summary>
    public void HandleTab(bool shift)
    {
        var selected = Text.AsSpan(SelectionStart, SelectionLength);
        if (shift)
        {
            Outdent();
        }
        else if (selected.Contains('\n'))
        {
            Indent();
        }
        else
        {
            var insert = "\t";
            if (Settings.InsertSpaces)
            {
                var lineStart = Lines.GetLineStart(Lines.GetLineFromOffset(SelectionStart));
                var column = 0;
                foreach (var c in Text.AsSpan(lineStart, SelectionStart - lineStart))
                {
                    column = c == '\t' ? (column / Settings.TabSize + 1) * Settings.TabSize : column + 1;
                }

                insert = new string(' ', Settings.TabSize - column % Settings.TabSize);
            }

            ApplyEdit(new TextEdit(SelectionStart, SelectionLength, insert, SelectionStart + insert.Length, 0));
        }
    }

    /// <summary>Enter key with automatic indentation. Returns false when the control should handle the key itself.</summary>
    public bool HandleEnter()
    {
        if (!Settings.AutoIndent)
        {
            return false;
        }

        var insert = "\n" + TextOperations.GetLineIndentation(Text, SelectionStart);
        ApplyEdit(new TextEdit(SelectionStart, SelectionLength, insert, SelectionStart + insert.Length, 0));
        return true;
    }

    // ---- Markdown formatting ------------------------------------------------------------------------

    public void ToggleBold() => ToggleInline("**");

    public void ToggleItalic() => ToggleInline("*");

    public void ToggleStrikethrough() => ToggleInline("~~");

    public void ToggleInlineCode() => ToggleInline("`");

    public void SetHeading(int level) => ApplyEdit(MarkdownFormatting.SetHeading(Text, SelectionStart, SelectionLength, level));

    public void ToggleQuote() => ApplyEdit(MarkdownFormatting.ToggleQuote(Text, SelectionStart, SelectionLength));

    public void ToggleBulletList() => ApplyEdit(MarkdownFormatting.ToggleBulletList(Text, SelectionStart, SelectionLength));

    public void ToggleNumberedList() => ApplyEdit(MarkdownFormatting.ToggleNumberedList(Text, SelectionStart, SelectionLength));

    public void ToggleTaskList() => ApplyEdit(MarkdownFormatting.ToggleTaskList(Text, SelectionStart, SelectionLength));

    public void InsertCodeBlock() => ApplyEdit(MarkdownFormatting.InsertCodeBlock(Text, SelectionStart, SelectionLength));

    public void InsertHorizontalRule() => ApplyEdit(MarkdownFormatting.InsertHorizontalRule(Text, SelectionStart, SelectionLength));

    public void InsertTable(int rows, int columns) =>
        ApplyEdit(MarkdownFormatting.InsertTable(Text, SelectionStart, SelectionLength, rows, columns, _services.Localizer.Get("Md_TableHeader")));

    public void InsertLink(LinkInput link) =>
        ApplyEdit(MarkdownFormatting.InsertLink(Text, SelectionStart, SelectionLength, link.Text, link.Url));

    public void InsertImage(string altText, string url) =>
        ApplyEdit(MarkdownFormatting.InsertImage(Text, SelectionStart, SelectionLength, altText, url));

    public string SelectedText => Text.Substring(SelectionStart, SelectionLength);

    // ---- Zoom ---------------------------------------------------------------------------------

    public void ZoomIn() => ZoomPercent = Math.Min(AppSettings.MaxZoom, (ZoomPercent / 10 + 1) * 10);

    public void ZoomOut() => ZoomPercent = Math.Max(AppSettings.MinZoom, ((ZoomPercent + 9) / 10 - 1) * 10);

    public void ResetZoom() => ZoomPercent = 100;

    // ---- Analysis -----------------------------------------------------------------------------

    /// <summary>Re-analyses now (settings that affect rendering changed). Styling is redone in full.</summary>
    public Task RefreshAnalysisAsync()
    {
        if (!IsMarkdown)
        {
            return Task.CompletedTask;
        }

        Analysis = null;
        _analysisDebouncer.Run(TimeSpan.Zero, RunAnalysisAsync);
        return _analysisDebouncer.Pending;
    }

    /// <summary>Completes when the analysis scheduled by the latest edit is done.</summary>
    public Task PendingAnalysis => _analysisDebouncer.Pending;

    public void Dispose()
    {
        _analysisDebouncer.Dispose();
        _statsDebouncer.Dispose();
        Find.Dispose();
        Editor = null;
        GC.SuppressFinalize(this);
    }

    partial void OnKindChanged(DocumentKind value)
    {
        OnPropertyChanged(nameof(IsMarkdown));
        OnPropertyChanged(nameof(IsRichText));
        if (value == DocumentKind.Markdown)
        {
            _ = RefreshAnalysisAsync();
        }
        else
        {
            _analysisDebouncer.Cancel();
            var previous = Analysis;
            Analysis = null;
            Headings = [];
            AnalysisChanged?.Invoke(this, new AnalysisChangedEventArgs(null, previous, null));
        }
    }

    partial void OnFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(PreviewResourceBase));
        OnPropertyChanged(nameof(Directory));
    }

    private string IndentUnit => Settings.InsertSpaces ? new string(' ', Settings.TabSize) : "\t";

    private void ToggleInline(string marker) =>
        ApplyEdit(MarkdownFormatting.ToggleInline(Text, SelectionStart, SelectionLength, marker, _services.Localizer.Get("Md_Placeholder")));

    private void Navigate(TextEdit? edit)
    {
        if (edit is not { } value)
        {
            return;
        }

        _navigatingHistory = true;
        try
        {
            if (Editor is not null)
            {
                Editor.ApplyEdit(value);
            }
            else
            {
                CommitText(value.Apply(Text));
                SetSelection(value.SelectionStart, value.SelectionLength);
            }
        }
        finally
        {
            _navigatingHistory = false;
        }

        UpdateHistoryState();
    }

    private void CommitText(string newText)
    {
        if (string.Equals(newText, Text, StringComparison.Ordinal))
        {
            return;
        }

        var change = TextDiff.Compute(Text, newText);
        var overwriteLength = GetOverwriteLength(newText, change);
        if (!_navigatingHistory)
        {
            _history.Record(change.Start, Text.Substring(change.Start, change.RemovedLength), newText.Substring(change.Start, change.InsertedLength), allowMerge: !_applyingCommand);
        }

        _pendingChange = Merge(_pendingChange, change);
        Text = newText;
        TextVersion++;
        _lineIndex = null;
        if (SelectionStart + SelectionLength > newText.Length)
        {
            SelectionStart = Math.Min(SelectionStart, newText.Length);
            SelectionLength = 0;
        }

        UpdateHistoryState();
        ScheduleStatistics();
        if (IsMarkdown)
        {
            _analysisDebouncer.Run(newText.Length > 200_000 ? TimeSpan.FromMilliseconds(600) : AnalysisDelay, RunAnalysisAsync);
        }

        Find.OnDocumentTextChanged();
        TextChanged?.Invoke(this, EventArgs.Empty);

        if (overwriteLength > 0)
        {
            Overwrite(change.Start + change.InsertedLength, overwriteLength);
        }
    }

    /// <summary>
    /// In overwrite mode, how many characters after freshly typed text must go: as many
    /// user-perceived characters as were typed, never past the end of the line.
    /// </summary>
    private int GetOverwriteLength(string newText, TextChange change)
    {
        if (!IsOverwrite || _navigatingHistory || _applyingCommand || change.RemovedLength != 0 || change.InsertedLength == 0)
        {
            return 0;
        }

        var typed = newText.AsSpan(change.Start, change.InsertedLength);
        if (typed.Contains('\n'))
        {
            return 0;
        }

        var rest = newText.AsSpan(change.Start + change.InsertedLength);
        var length = 0;
        while (!typed.IsEmpty && length < rest.Length && rest[length] != '\n')
        {
            typed = typed[System.Globalization.StringInfo.GetNextTextElementLength(typed)..];
            length += System.Globalization.StringInfo.GetNextTextElementLength(rest[length..]);
        }

        return length;
    }

    /// <summary>Removes the overwritten characters as part of the undo step that typed over them.</summary>
    private void Overwrite(int start, int length)
    {
        var removed = Text.Substring(start, length);
        var edit = new TextEdit(start, length, "", start, 0);
        _navigatingHistory = true;
        try
        {
            if (Editor is not null)
            {
                Editor.ApplyEdit(edit);
            }
            else
            {
                CommitText(edit.Apply(Text));
                SetSelection(start, 0);
            }
        }
        finally
        {
            _navigatingHistory = false;
        }

        _history.ExtendLastRemoved(removed);
        UpdateHistoryState();
    }

    /// <summary>Combines a new change with the ones not yet analysed into one region of old and new text.</summary>
    private static TextChange Merge(TextChange? pending, TextChange change)
    {
        if (pending is not { } p)
        {
            return change;
        }

        var start = Math.Min(p.Start, change.Start);
        var oldEnd = p.Start + p.RemovedLength;
        var newEnd = p.Start + p.InsertedLength;
        var changeEnd = change.Start + change.RemovedLength;
        if (changeEnd > newEnd)
        {
            oldEnd += changeEnd - newEnd;
            newEnd = changeEnd;
        }

        newEnd += change.Delta;
        return new TextChange(start, oldEnd - start, newEnd - start);
    }

    private void SetSelection(int start, int length)
    {
        start = Math.Clamp(start, 0, Text.Length);
        SelectionStart = start;
        SelectionLength = Math.Clamp(length, 0, Text.Length - start);

        var caret = SelectionStart + SelectionLength;
        var line = Lines.GetLineFromOffset(caret);
        Line = line + 1;
        Column = LineIndex.GetColumn(Text, Lines.GetLineStart(line), caret);
    }

    private void UpdateHistoryState()
    {
        if (IsRichText)
        {
            // The native history of the control is the only one that knows about formatting changes.
            var rich = Editor as IRichEditorSurface;
            CanUndo = rich?.CanUndoRich == true;
            CanRedo = rich?.CanRedoRich == true;
            IsModified = !string.Equals(_richText, _savedRichText, StringComparison.Ordinal);
            return;
        }
        CanUndo = _history.CanUndo;
        CanRedo = _history.CanRedo;
        IsModified = _metadataModified || !_history.IsAtSavedState;
    }

    private void ScheduleStatistics()
    {
        if (Text.Length <= 20_000)
        {
            _statsDebouncer.Cancel();
            ApplyStatistics(TextStatistics.Compute(Text));
            return;
        }

        _statsDebouncer.Run(TimeSpan.FromMilliseconds(300), async token =>
        {
            var text = Text;
            var stats = await Task.Run(() => TextStatistics.Compute(text, token), token);
            if (!token.IsCancellationRequested)
            {
                ApplyStatistics(stats);
            }
        });
    }

    private void ApplyStatistics(TextStats stats)
    {
        CharacterCount = stats.Characters;
        WordCount = stats.Words;
        ReadingMinutes = stats.ReadingMinutes;
    }

    private async Task RunAnalysisAsync(CancellationToken token)
    {
        var text = Text;
        var version = TextVersion;
        var options = new MarkdownRenderOptions(
            Settings.CodeHighlighting,
            PreviewResourceBase,
            IncludeSyntaxRuns: Settings.MarkdownSyntaxHighlighting && text.Length <= FileLimits.MaxHighlightChars);

        var analysis = await Task.Run(() => _services.Markdown.Analyze(text, options, token), token);
        if (token.IsCancellationRequested || version != TextVersion || !IsMarkdown)
        {
            return;
        }

        var previous = Analysis;
        var change = _pendingChange;
        _pendingChange = null;
        Analysis = analysis;
        Headings = analysis.Headings;

        (int, int)? dirty = previous is not null && change is { } c
            ? HighlightPlanner.GetDirtyRange(previous.Runs, analysis.Runs, c, text.Length)
            : null;
        AnalysisChanged?.Invoke(this, new AnalysisChangedEventArgs(analysis, previous, dirty));
    }
}
