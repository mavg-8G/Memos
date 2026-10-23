using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Memos.Core.Helpers;
using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Memos.Core.ViewModels;

/// <summary>The window: open documents, the active one, and every command of menus, toolbar and shortcuts.</summary>
public partial class MainViewModel : ObservableObject
{
    private static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd"];

    private readonly IFileService _files;
    private readonly IRecentFilesService _recent;
    private readonly ISessionService _session;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _pickers;
    private readonly IClipboardService _clipboard;
    private readonly IPdfExporter _pdf;
    private readonly IPrintService _print;
    private readonly IMarkdownService _markdown;
    private readonly ILocalizer _loc;
    private readonly IErrorLog _errorLog;
    private readonly DocumentServices _documentServices;
    private readonly Debouncer _sessionDebouncer = new();
    private readonly Dictionary<Guid, Debouncer> _autoSaveDebouncers = [];
    private readonly List<ClosedDocument> _closed = [];
    private int _untitledCount;

    public MainViewModel(
        ISettingsService settings, IFileService files, IRecentFilesService recent, ISessionService session,
        IDialogService dialogs, IFilePickerService pickers, IClipboardService clipboard, IPdfExporter pdf, IPrintService print,
        ISearchService search, IMarkdownService markdown, ILocalizer localizer, IErrorLog errorLog)
    {
        _errorLog = errorLog;
        _print = print;
        GlobalSearch = new GlobalSearchViewModel(() => Documents, document => ActiveDocument = document, search, dialogs, localizer);
        HasErrorLog = errorLog.HasEntries;
        _files = files;
        _recent = recent;
        _session = session;
        _dialogs = dialogs;
        _pickers = pickers;
        _clipboard = clipboard;
        _pdf = pdf;
        _markdown = markdown;
        _loc = localizer;
        Settings = settings.Current;
        _documentServices = new DocumentServices(settings, search, markdown, localizer);

        RecentFiles = new ObservableCollection<string>(recent.Items);
        recent.Changed += (_, _) =>
        {
            RecentFiles.Clear();
            foreach (var item in recent.Items)
            {
                RecentFiles.Add(item);
            }
        };

        Settings.PropertyChanged += OnSettingsChanged;
    }

    public AppSettings Settings { get; }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public ObservableCollection<string> RecentFiles { get; }

    [ObservableProperty] public partial DocumentViewModel? ActiveDocument { get; set; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    [ObservableProperty] public partial bool IsMarkdownActive { get; private set; }
    [ObservableProperty] public partial bool IsRichTextActive { get; private set; }
    [ObservableProperty] public partial bool HasErrorLog { get; private set; }

    /// <summary>Find and replace across all open tabs.</summary>
    public GlobalSearchViewModel GlobalSearch { get; }

    public string ErrorLogPath => _errorLog.FilePath;

    public bool HasDocument => ActiveDocument is not null;

    public bool CanUndo => ActiveDocument?.CanUndo == true;

    public bool CanRedo => ActiveDocument?.CanRedo == true;

    public bool CanReopenClosed => _closed.Count > 0;

    public bool HasFile => ActiveDocument?.FilePath is not null;

    /// <summary>The window should close now; raised by the Exit command once nothing is left to confirm.</summary>
    public event EventHandler? CloseWindowRequested;

    // ---- Start-up and shutdown ----------------------------------------------------------------------

    /// <summary>Restores the previous session if enabled, opens the files passed to the app, and guarantees one tab.</summary>
    public async Task InitializeAsync(IReadOnlyList<string> startupPaths)
    {
        if (Settings.RestoreSession)
        {
            await RestoreSessionAsync();
        }

        if (startupPaths.Count > 0)
        {
            await OpenPathsAsync(startupPaths);
        }

        if (Documents.Count == 0)
        {
            NewDocument(Settings.StartupDocument);
        }

        ActiveDocument ??= Documents[0];
    }

    /// <summary>
    /// Called when the window is about to close. Returns false if the user cancelled. With session
    /// restore on, unsaved work is kept in the session instead of asking.
    /// </summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        _sessionDebouncer.Cancel();
        if (Settings.RestoreSession)
        {
            await SaveSessionAsync();
            return true;
        }

        foreach (var document in Documents.Where(d => d.IsModified).ToList())
        {
            if (!await ConfirmDiscardOrSaveAsync(document))
            {
                return false;
            }
        }

        _session.Clear();
        return true;
    }

    public async Task SaveSessionAsync()
    {
        var state = new SessionState();
        var backups = new Dictionary<Guid, string>();
        foreach (var document in Documents)
        {
            if (document.FilePath is null && document.Text.Length == 0 && !document.IsModified)
            {
                continue;
            }

            var hasBackup = document.IsModified || document.FilePath is null;
            if (hasBackup && document.BackupVersion != document.TextVersion)
            {
                backups[document.Id] = document.IsRichText ? document.RichText : document.Text;
                document.BackupVersion = document.TextVersion;
            }

            if (document == ActiveDocument)
            {
                state.ActiveIndex = state.Documents.Count;
            }

            state.Documents.Add(new SessionDocument
            {
                Id = document.Id,
                FilePath = document.FilePath,
                DisplayName = document.DisplayName,
                Kind = document.Kind,
                HasBackup = hasBackup,
                Encoding = document.Encoding,
                LineEnding = document.LineEnding,
                SelectionStart = document.SelectionStart,
                SelectionLength = document.SelectionLength,
                ViewMode = document.ViewMode,
            });
        }

        try
        {
            await _session.SaveAsync(state, backups);
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
            // Backups will be retried on the next change; force them to be written again.
            foreach (var id in backups.Keys)
            {
                if (Documents.FirstOrDefault(d => d.Id == id) is { } document)
                {
                    document.BackupVersion = -1;
                }
            }
        }
    }

    /// <summary>Tells the user when the active file was changed by another program.</summary>
    public void CheckExternalChanges()
    {
        if (ActiveDocument is not { FilePath: { } path, Stamp: { } known } document ||
            _files.GetStamp(path) is not { } current || current == known || current == document.NotifiedStamp)
        {
            return;
        }

        document.NotifiedStamp = current;
        document.Notice = new DocumentNotice(_loc.Get("Notice_ExternalChange"), NoticeSeverity.Warning,
            _loc.Get("Btn_Reload"), () => ReloadAsync(document, confirm: document.IsModified));
    }

    // ---- File commands ------------------------------------------------------------------------------

    [RelayCommand]
    private void NewText() => NewDocument(DocumentKind.Text);

    [RelayCommand]
    private void NewMarkdown() => NewDocument(DocumentKind.Markdown);

    [RelayCommand]
    private void NewRichText() => NewDocument(DocumentKind.RichText);

    /// <summary>The "+" button: a new document of the same kind as the one being edited.</summary>
    [RelayCommand]
    private void NewTab() => NewDocument(ActiveDocument?.Kind ?? Settings.StartupDocument);

    [RelayCommand]
    private async Task OpenAsync()
    {
        var paths = await _pickers.PickOpenFilesAsync();
        if (paths.Count > 0)
        {
            await OpenPathsAsync(paths);
        }
    }

    [RelayCommand]
    private Task OpenRecentAsync(string path) => OpenPathsAsync([path]);

    [RelayCommand]
    private void ClearRecent() => _recent.Clear();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task SaveAsync() => SaveDocumentAsync(ActiveDocument!, saveAs: false);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task SaveAsAsync() => SaveDocumentAsync(ActiveDocument!, saveAs: true);

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        foreach (var document in Documents.Where(d => d.IsModified || d.FilePath is null && d.Text.Length > 0).ToList())
        {
            ActiveDocument = document;
            if (!await SaveDocumentAsync(document, saveAs: false))
            {
                break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task CloseTabAsync() => CloseDocumentAsync(ActiveDocument!);

    [RelayCommand]
    private Task CloseSpecificAsync(DocumentViewModel document) => CloseDocumentAsync(document);

    [RelayCommand]
    private async Task CloseOthersAsync(DocumentViewModel keep)
    {
        foreach (var document in Documents.Where(d => d != keep).ToList())
        {
            if (!await CloseDocumentAsync(document))
            {
                break;
            }
        }
    }

    [RelayCommand]
    private async Task CloseToRightAsync(DocumentViewModel from)
    {
        var index = Documents.IndexOf(from);
        foreach (var document in Documents.Skip(index + 1).ToList())
        {
            if (!await CloseDocumentAsync(document))
            {
                break;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanReopenClosed))]
    private async Task ReopenClosedAsync()
    {
        var closed = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        ReopenClosedCommand.NotifyCanExecuteChanged();

        if (closed.FilePath is not null && closed.Text is null)
        {
            await OpenPathsAsync([closed.FilePath]);
        }
        else
        {
            var document = AddDocument(closed.Kind, closed.DisplayName);
            document.FilePath = closed.FilePath;
            if (closed.FilePath is not null)
            {
                document.Stamp = _files.GetStamp(closed.FilePath);
            }

            if (closed.Kind == DocumentKind.RichText) document.RestoreRichText(closed.Rtf ?? "", modified: true);
            else document.ResetText(closed.Text ?? "", modified: true);
            ActiveDocument = document;
        }

        ActiveDocument?.Select(closed.SelectionStart, 0);
    }

    [RelayCommand]
    private void NextTab() => CycleTab(1);

    [RelayCommand]
    private void PreviousTab() => CycleTab(-1);

    [RelayCommand]
    private async Task ExitAsync()
    {
        if (await PrepareToCloseAsync())
        {
            CloseWindowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- Edit commands ------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => ActiveDocument?.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => ActiveDocument?.Redo();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Cut() => ActiveDocument?.Editor?.Cut();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Copy() => ActiveDocument?.Editor?.Copy();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Paste() => ActiveDocument?.Editor?.Paste();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SelectAll() => ActiveDocument?.Editor?.SelectAll();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Find() => ShowFind(replace: false);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Replace() => ShowFind(replace: true);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task GoToLineAsync()
    {
        var document = ActiveDocument!;
        if (await _dialogs.PromptGoToLineAsync(document.Line, document.LineCount) is { } line)
        {
            document.GoToLine(line);
            document.Editor?.Focus();
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void DuplicateLine() => ActiveDocument?.DuplicateLines();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void DeleteLine() => ActiveDocument?.DeleteLines();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void MoveLineUp() => ActiveDocument?.MoveLines(up: true);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void MoveLineDown() => ActiveDocument?.MoveLines(up: false);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Uppercase() => ActiveDocument?.ChangeCase(upper: true);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Lowercase() => ActiveDocument?.ChangeCase(upper: false);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void TrimTrailingWhitespace() => ActiveDocument?.TrimTrailingWhitespace();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Indent() => ActiveDocument?.Indent();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Outdent() => ActiveDocument?.Outdent();

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void IndentationToSpaces() => ActiveDocument?.ConvertIndentation(toSpaces: true);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void IndentationToTabs() => ActiveDocument?.ConvertIndentation(toSpaces: false);

    // ---- View commands ------------------------------------------------------------------------------

    /// <summary>Zoom scales the editor font, and an RTF document has fonts and sizes of its own.</summary>
    private bool CanZoom => HasDocument && !IsRichTextActive;

    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void ZoomIn() => ActiveDocument?.ZoomIn();

    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void ZoomOut() => ActiveDocument?.ZoomOut();

    [RelayCommand(CanExecute = nameof(CanZoom))]
    private void ResetZoom() => ActiveDocument?.ResetZoom();

    [RelayCommand]
    private void OpenSettings()
    {
        HasErrorLog = _errorLog.HasEntries;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void FindInTabs()
    {
        IsSettingsOpen = false;
        GlobalSearch.Show(ActiveDocument?.SelectedText);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void ToggleOverwrite()
    {
        if (ActiveDocument is { } document)
        {
            document.IsOverwrite = !document.IsOverwrite;
        }
    }

    // ---- Error log ----------------------------------------------------------------------------------

    /// <summary>Opens the error log in a tab so it can be read or attached to a report.</summary>
    [RelayCommand]
    private async Task OpenErrorLogAsync()
    {
        if (_errorLog.HasEntries)
        {
            IsSettingsOpen = false;
            await OpenPathsAsync([_errorLog.FilePath]);
        }
    }

    [RelayCommand]
    private void CopyErrorLog()
    {
        if (_errorLog.Read() is { Length: > 0 } text)
        {
            _clipboard.SetText(text);
        }
    }

    [RelayCommand]
    private void ClearErrorLog()
    {
        _errorLog.Clear();
        HasErrorLog = _errorLog.HasEntries;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
        ActiveDocument?.Editor?.Focus();
    }

    /// <summary>
    /// Converting to or from RTF keeps the text and leaves the original file untouched. Leaving RTF
    /// drops the formatting, so it asks first when there is any to lose.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task SetKindAsync(DocumentKind kind)
    {
        if (ActiveDocument is not { } document || document.Kind == kind)
        {
            return;
        }

        if (kind == DocumentKind.RichText)
        {
            document.ConvertToRichText();
        }
        else if (!document.IsRichText)
        {
            document.Kind = kind;
        }
        else if (!document.HasRichFormatting || await _dialogs.ConfirmAsync(_loc.Get("Msg_DropFormatting_Title"),
                     _loc.Get("Msg_DropFormatting"), _loc.Get("Btn_DropFormatting"), _loc.Get("Btn_Cancel")))
        {
            document.ConvertToPlainText(kind);
        }
    }

    private bool CanToggleFormatting => HasDocument && !IsMarkdownActive;

    /// <summary>The formatting switch of the text workspace: plain text on one side, RTF on the other.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleFormatting))]
    private Task ToggleFormattingAsync() =>
        SetKindAsync(IsRichTextActive ? DocumentKind.Text : DocumentKind.RichText);

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void SetViewMode(MarkdownViewMode mode)
    {
        if (ActiveDocument is { } document)
        {
            document.ViewMode = mode;
        }
    }

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void ToggleOutline()
    {
        if (ActiveDocument is { } document)
        {
            document.IsOutlineVisible = !document.IsOutlineVisible;
        }
    }

    // ---- Encoding and line endings -----------------------------------------------------------------

    /// <summary>Chooses the encoding the document will be saved with, warning first if characters would be lost.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task SetEncodingAsync(TextEncodingId encoding)
    {
        var document = ActiveDocument!;
        if (document.Encoding == encoding)
        {
            return;
        }

        if (!TextEncodings.CanEncode(document.Text, encoding) &&
            !await _dialogs.ConfirmAsync(_loc.Get("Msg_LossyEncoding_Title"),
                _loc.Format("Msg_LossyEncoding", TextEncodings.GetDisplayName(encoding)),
                _loc.Get("Btn_Convert"), _loc.Get("Btn_Cancel")))
        {
            return;
        }

        document.SetEncoding(encoding);
    }

    /// <summary>Reads the file again interpreting its bytes with another encoding.</summary>
    [RelayCommand(CanExecute = nameof(HasFile))]
    private async Task ReopenWithEncodingAsync(TextEncodingId encoding)
    {
        if (ActiveDocument is { FilePath: not null } document)
        {
            await ReloadAsync(document, confirm: document.IsModified, encoding);
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SetLineEnding(LineEnding lineEnding) => ActiveDocument?.SetLineEnding(lineEnding);

    // ---- Markdown commands --------------------------------------------------------------------------

    private bool CanFormatText => IsMarkdownActive || IsRichTextActive;

    [RelayCommand(CanExecute = nameof(CanFormatText))]
    private void Bold()
    {
        if (IsRichTextActive) ActiveDocument?.ToggleRichStyle(RichStyle.Bold);
        else ActiveDocument?.ToggleBold();
    }

    [RelayCommand(CanExecute = nameof(CanFormatText))]
    private void Italic()
    {
        if (IsRichTextActive) ActiveDocument?.ToggleRichStyle(RichStyle.Italic);
        else ActiveDocument?.ToggleItalic();
    }

    [RelayCommand(CanExecute = nameof(CanFormatText))]
    private void Strikethrough()
    {
        if (IsRichTextActive) ActiveDocument?.ToggleRichStyle(RichStyle.Strikethrough);
        else ActiveDocument?.ToggleStrikethrough();
    }

    [RelayCommand(CanExecute = nameof(IsRichTextActive))]
    private void Underline() => ActiveDocument?.ToggleRichStyle(RichStyle.Underline);

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void InlineCode() => ActiveDocument?.ToggleInlineCode();

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void Heading(string level) => ActiveDocument?.SetHeading(int.Parse(level));

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void Quote() => ActiveDocument?.ToggleQuote();

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void BulletList() => ActiveDocument?.ToggleBulletList();

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void NumberedList() => ActiveDocument?.ToggleNumberedList();

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void TaskList() => ActiveDocument?.ToggleTaskList();

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void CodeBlock() => ActiveDocument?.InsertCodeBlock();

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void HorizontalRule() => ActiveDocument?.InsertHorizontalRule();

    public void InsertTable(int rows, int columns)
    {
        if (ActiveDocument is { IsMarkdown: true } document)
        {
            document.InsertTable(rows, columns);
            document.Editor?.Focus();
        }
    }

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private async Task LinkAsync()
    {
        var document = ActiveDocument!;
        if (await _dialogs.PromptLinkAsync(document.SelectedText.ReplaceLineEndings(" ")) is { } link)
        {
            document.InsertLink(link);
        }

        document.Editor?.Focus();
    }

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private async Task ImageAsync()
    {
        var document = ActiveDocument!;
        if (await _pickers.PickImageAsync(document.Directory) is not { } path)
        {
            return;
        }

        // Prefer a path relative to the document so the Markdown stays portable.
        var url = path;
        if (document.Directory is { } directory)
        {
            var relative = Path.GetRelativePath(directory, path);
            if (!Path.IsPathRooted(relative))
            {
                url = relative;
            }
        }

        var alt = document.SelectionLength > 0 ? document.SelectedText.ReplaceLineEndings(" ") : Path.GetFileNameWithoutExtension(path);
        document.InsertImage(alt, url);
        document.Editor?.Focus();
    }

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private async Task ExportHtmlAsync()
    {
        var document = ActiveDocument!;
        if (await _pickers.PickExportFileAsync(GetBaseName(document), ExportFormat.Html, document.Directory) is not { } path)
        {
            return;
        }

        try
        {
            var html = BuildExportHtml(document);
            await _files.SaveAsync(path, html, TextEncodingId.Utf8, LineEnding.Lf);
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
            await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), _loc.Format("Msg_ExportError", ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private async Task ExportPdfAsync()
    {
        var document = ActiveDocument!;
        if (await _pickers.PickExportFileAsync(GetBaseName(document), ExportFormat.Pdf, document.Directory) is not { } path)
        {
            return;
        }

        try
        {
            await _pdf.ExportAsync(MarkdownPrintBuilder.Build(document.Text, Settings.CodeHighlighting), document.Directory, path);
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex) || ex is InvalidOperationException)
        {
            await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), _loc.Format("Msg_ExportError", ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task PrintAsync()
    {
        var document = ActiveDocument!;
        try
        {
            if (document.IsMarkdown)
            {
                await _print.PrintMarkdownAsync(document.DisplayName, MarkdownPrintBuilder.Build(document.Text), document.Directory);
            }
            else if (document.IsRichText)
            {
                await _print.PrintTextAsync(document.DisplayName, RichTextCodec.Parse(document.RichText), monospaced: false);
            }
            else
            {
                await _print.PrintTextAsync(document.DisplayName, [new RichTextRun(document.Text)], monospaced: true);
            }
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex) || ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), _loc.Format("Msg_PrintError", ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(IsMarkdownActive))]
    private void CopyHtml()
    {
        var document = ActiveDocument!;
        var analysis = _markdown.Analyze(document.Text, new MarkdownRenderOptions(CodeHighlighting: false, IncludeFrontMatter: false));
        // Rich editors receive formatted content; plain-text targets receive the HTML source.
        var html = string.Concat(analysis.Blocks.Select(b => b.Html));
        _clipboard.SetHtml(html, html);
    }

    /// <summary>The complete HTML document of the HTML export.</summary>
    public string BuildExportHtml(DocumentViewModel document)
    {
        var analysis = _markdown.Analyze(document.Text, new MarkdownRenderOptions(Settings.CodeHighlighting, IncludeFrontMatter: false));

        // Front matter is metadata: it is not part of the exported body, but its title names the page.
        var title = analysis.FrontMatter?["title"] is { Length: > 0 } declared ? declared : GetBaseName(document);
        return HtmlDocumentBuilder.BuildPage(title, HtmlDocumentBuilder.WrapBlocks(analysis.Blocks), new HtmlPageOptions
        {
            Style = Settings.PreviewStyle,
            FontSize = Settings.PreviewFontSize,
            AllowRemoteImages = Settings.AllowRemoteImages,
            Language = _loc.Get("LanguageTag"),
        });
    }

    // ---- Documents ----------------------------------------------------------------------------------

    public static DocumentKind GetKindForPath(string path) =>
        Path.GetExtension(path).Equals(".rtf", StringComparison.OrdinalIgnoreCase) ? DocumentKind.RichText
        : MarkdownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? DocumentKind.Markdown : DocumentKind.Text;

    public DocumentViewModel NewDocument(DocumentKind kind)
    {
        _untitledCount++;
        var name = _untitledCount == 1 ? _loc.Get("Untitled") : $"{_loc.Get("Untitled")} {_untitledCount}";
        var document = AddDocument(kind, name);
        ActiveDocument = document;
        return document;
    }

    /// <summary>Opens files in tabs. A file that is already open is simply brought to the front.</summary>
    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        foreach (var raw in paths)
        {
            string path;
            try
            {
                path = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (FileService.IsFileSystemError(ex))
            {
                await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), _loc.Format("Msg_ReadError", raw, ex.Message));
                continue;
            }

            if (FindOpenDocument(path) is { } existing)
            {
                ActiveDocument = existing;
                continue;
            }

            var kind = GetKindForPath(path);
            if (kind == DocumentKind.RichText)
            {
                if (await LoadRichTextAsync(path) is not { } rich) continue;
                var oldBlank = Documents.Count == 1 && Documents[0].IsPristine ? Documents[0] : null;
                var richDocument = AddDocument(kind, Path.GetFileName(path));
                richDocument.LoadRichText(rich, path);
                ActiveDocument = richDocument;
                if (oldBlank is not null) RemoveDocument(oldBlank);
                _recent.Add(path);
                continue;
            }

            if (await LoadAsync(path, null) is not { } loaded)
            {
                continue;
            }

            // An untouched blank tab gives way to the file, as Notepad does.
            var pristine = Documents.Count == 1 && Documents[0].IsPristine ? Documents[0] : null;
            var document = AddDocument(kind, Path.GetFileName(path));
            ApplyLoaded(document, loaded, path);
            ActiveDocument = document;
            if (pristine is not null)
            {
                RemoveDocument(pristine);
            }

            _recent.Add(path);
        }
    }

    public async Task<bool> SaveDocumentAsync(DocumentViewModel document, bool saveAs, bool quiet = false)
    {
        var pickedPath = document.FilePath is null || saveAs;
        string path;
        if (!pickedPath)
        {
            path = document.FilePath!;
        }
        else
        {
            if (quiet)
            {
                return false;
            }

            var suggested = Path.HasExtension(document.DisplayName) ? document.DisplayName : GetBaseName(document);
            if (await _pickers.PickSaveFileAsync(suggested, document.Kind, document.Directory) is not { } picked)
            {
                return false;
            }

            path = picked;
            if (FindOpenDocument(path) is { } other && other != document)
            {
                await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), _loc.Format("Msg_AlreadyOpen", Path.GetFileName(path)));
                return false;
            }
        }

        var text = document.Text;
        if (!document.IsRichText && Path.GetExtension(path).Equals(".rtf", StringComparison.OrdinalIgnoreCase))
        {
            document.ConvertToRichText();
        }
        if (document.IsRichText && !Path.GetExtension(path).Equals(".rtf", StringComparison.OrdinalIgnoreCase))
        {
            await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), _loc.Get("Msg_RtfExtension"));
            return false;
        }
        if (!document.IsRichText && !TextEncodings.CanEncode(text, document.Encoding))
        {
            if (quiet || !await _dialogs.ConfirmAsync(_loc.Get("Msg_LossyEncoding_Title"),
                    _loc.Format("Msg_LossySave", TextEncodings.GetDisplayName(document.Encoding)),
                    _loc.Get("Btn_SaveAnyway"), _loc.Get("Btn_Cancel")))
            {
                return false;
            }
        }

        // Never silently overwrite what another program wrote since we read the file.
        if (path == document.FilePath && document.Stamp is { } known && _files.GetStamp(path) is { } current && current != known)
        {
            if (quiet || !await _dialogs.ConfirmAsync(_loc.Get("Msg_ExternalChange_Title"),
                    _loc.Format("Msg_ExternalChangeOverwrite", document.DisplayName),
                    _loc.Get("Btn_Overwrite"), _loc.Get("Btn_Cancel")))
            {
                return false;
            }
        }

        try
        {
            var rtf = document.IsRichText ? document.RichText : null;
            var stamp = document.IsRichText
                ? await _files.SaveRichTextAsync(path, rtf!)
                : await _files.SaveAsync(path, text, document.Encoding, document.LineEnding);
            if (ReferenceEquals(text, document.Text) && (!document.IsRichText || string.Equals(rtf, document.RichText, StringComparison.Ordinal)))
            {
                document.MarkSaved(path, stamp);
            }
            else
            {
                // Edited while the write was in flight: the file is valid but the document is still dirty.
                document.FilePath = path;
                document.DisplayName = Path.GetFileName(path);
                document.Stamp = stamp;
            }
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
            var message = ex switch
            {
                ReadOnlyFileException => _loc.Format("Msg_ReadOnly", Path.GetFileName(path)),
                UnauthorizedAccessException => _loc.Format("Msg_AccessDenied", path),
                _ => _loc.Format("Msg_SaveError", Path.GetFileName(path), ex.Message),
            };

            if (quiet)
            {
                document.Notice = new DocumentNotice(message, NoticeSeverity.Error);
            }
            else
            {
                await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), message);
            }

            return false;
        }

        if (pickedPath)
        {
            // The new name decides the workspace: saving as .md turns the tab into a Markdown document.
            document.Kind = GetKindForPath(path);
        }

        if (document.Notice?.Severity is NoticeSeverity.Warning or NoticeSeverity.Error)
        {
            document.Notice = null;
        }

        _recent.Add(path);
        RefreshCommands();
        return true;
    }

    /// <summary>Closes a tab, asking about unsaved changes. Returns false if the user cancelled.</summary>
    public async Task<bool> CloseDocumentAsync(DocumentViewModel document)
    {
        if (document.IsModified && Settings.ConfirmCloseModified && !await ConfirmDiscardOrSaveAsync(document))
        {
            return false;
        }

        if (!document.IsPristine)
        {
            // A file without unsaved changes is reopened from disk; anything else keeps its text.
            var keepText = document.FilePath is null || document.IsModified;
            _closed.Add(new ClosedDocument(document.FilePath, document.DisplayName, document.Kind, keepText ? document.Text : null, document.SelectionStart,
                document.IsRichText && keepText ? document.RichText : null));
            if (_closed.Count > 10)
            {
                _closed.RemoveAt(0);
            }
        }

        RemoveDocument(document);
        if (Documents.Count == 0)
        {
            NewDocument(Settings.StartupDocument);
        }

        RefreshCommands();
        return true;
    }

    public void MoveDocument(int oldIndex, int newIndex)
    {
        if (oldIndex != newIndex && oldIndex >= 0 && newIndex >= 0 && oldIndex < Documents.Count && newIndex < Documents.Count)
        {
            Documents.Move(oldIndex, newIndex);
        }
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActiveDocumentPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActiveDocumentPropertyChanged;
        }

        IsMarkdownActive = newValue?.IsMarkdown == true;
        IsRichTextActive = newValue?.IsRichText == true;
        RefreshCommands();
    }

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.CanUndo):
                UndoCommand.NotifyCanExecuteChanged();
                break;
            case nameof(DocumentViewModel.CanRedo):
                RedoCommand.NotifyCanExecuteChanged();
                break;
            case nameof(DocumentViewModel.Kind):
                IsMarkdownActive = ActiveDocument?.IsMarkdown == true;
                IsRichTextActive = ActiveDocument?.IsRichText == true;
                RefreshCommands();
                break;
            case nameof(DocumentViewModel.FilePath):
                ReopenWithEncodingCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Settings that change what the Markdown analysis produces.
        if (e.PropertyName is nameof(AppSettings.CodeHighlighting) or nameof(AppSettings.MarkdownSyntaxHighlighting))
        {
            foreach (var document in Documents)
            {
                _ = document.RefreshAnalysisAsync();
            }
        }
    }

    private DocumentViewModel AddDocument(DocumentKind kind, string name)
    {
        var document = new DocumentViewModel(_documentServices, kind, name);
        document.TextChanged += OnDocumentTextChanged;
        Documents.Add(document);
        return document;
    }

    private void RemoveDocument(DocumentViewModel document)
    {
        var index = Documents.IndexOf(document);
        if (index < 0)
        {
            return;
        }

        document.TextChanged -= OnDocumentTextChanged;
        if (_autoSaveDebouncers.Remove(document.Id, out var debouncer))
        {
            debouncer.Dispose();
        }

        Documents.RemoveAt(index);
        if (ActiveDocument == document)
        {
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        }

        document.Dispose();
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        var document = (DocumentViewModel)sender!;
        if (Settings.RestoreSession)
        {
            // Crash protection: keep the session backup a few seconds behind the user's typing.
            _sessionDebouncer.Run(TimeSpan.FromSeconds(4), _ => SaveSessionAsync());
        }

        if (Settings.AutoSave && document.FilePath is not null)
        {
            if (!_autoSaveDebouncers.TryGetValue(document.Id, out var debouncer))
            {
                _autoSaveDebouncers[document.Id] = debouncer = new Debouncer();
            }

            debouncer.Run(TimeSpan.FromSeconds(2), async _ =>
            {
                if (document.IsModified && Documents.Contains(document))
                {
                    await SaveDocumentAsync(document, saveAs: false, quiet: true);
                }
            });
        }
    }

    private async Task RestoreSessionAsync()
    {
        if (await _session.LoadAsync() is not { } state)
        {
            return;
        }

        DocumentViewModel? active = null;
        for (var i = 0; i < state.Documents.Count; i++)
        {
            var saved = state.Documents[i];
            DocumentViewModel? document = null;
            if (saved.HasBackup && await _session.ReadBackupAsync(saved.Id) is { } backup)
            {
                document = new DocumentViewModel(_documentServices, saved.Kind, saved.DisplayName) { Id = saved.Id };
                document.TextChanged += OnDocumentTextChanged;
                Documents.Add(document);
                document.FilePath = saved.FilePath;
                document.SetEncoding(saved.Encoding);
                document.SetLineEnding(saved.LineEnding);
                if (saved.Kind == DocumentKind.RichText) document.RestoreRichText(backup, modified: true);
                else document.ResetText(backup, modified: true);
                document.BackupVersion = document.TextVersion;
                if (saved.FilePath is not null)
                {
                    document.Stamp = _files.GetStamp(saved.FilePath);
                }
            }
            else if (saved.FilePath is not null && FindOpenDocument(saved.FilePath) is null && _files.GetLength(saved.FilePath) is not null)
            {
                try
                {
                    // Read first: a file that cannot be loaded must not leave an empty tab behind.
                    if (saved.Kind == DocumentKind.RichText)
                    {
                        var rich = await _files.LoadRichTextAsync(saved.FilePath);
                        document = AddDocument(saved.Kind, saved.DisplayName);
                        document.LoadRichText(rich, saved.FilePath);
                    }
                    else
                    {
                        var loaded = await _files.LoadAsync(saved.FilePath);
                        document = AddDocument(saved.Kind, saved.DisplayName);
                        ApplyLoaded(document, loaded, saved.FilePath);
                    }
                }
                catch (Exception ex) when (FileService.IsFileSystemError(ex))
                {
                    // The file is gone or unreadable now: the tab is simply not restored.
                }
            }

            if (document is null)
            {
                continue;
            }

            document.ViewMode = saved.ViewMode;
            document.Select(saved.SelectionStart, saved.SelectionLength);
            if (i == state.ActiveIndex)
            {
                active = document;
            }

            if (document.FilePath is null)
            {
                _untitledCount++;
            }
        }

        ActiveDocument = active ?? Documents.FirstOrDefault();
    }

    private async Task ReloadAsync(DocumentViewModel document, bool confirm, TextEncodingId? encoding = null)
    {
        if (document.FilePath is not { } path)
        {
            return;
        }

        if (confirm && !await _dialogs.ConfirmAsync(_loc.Get("Msg_ReloadDiscard_Title"),
                _loc.Format("Msg_ReloadDiscard", document.DisplayName), _loc.Get("Btn_Reload"), _loc.Get("Btn_Cancel")))
        {
            return;
        }

        if (document.IsRichText)
        {
            if (await LoadRichTextAsync(path) is { } rich) document.LoadRichText(rich, path);
        }
        else if (await LoadAsync(path, encoding) is { } loaded)
        {
            ApplyLoaded(document, loaded, path);
        }
    }

    /// <summary>Reads a file, reporting problems to the user. Returns null if it was not loaded.</summary>
    private async Task<LoadedText?> LoadAsync(string path, TextEncodingId? encoding)
    {
        var name = Path.GetFileName(path);
        var length = _files.GetLength(path);
        if (length is null)
        {
            _recent.Remove(path);
            await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), _loc.Format("Msg_FileNotFound", path));
            return null;
        }

        if (length > FileLimits.WarnBytes && length <= FileLimits.MaxBytes &&
            !await _dialogs.ConfirmAsync(_loc.Get("Msg_LargeFile_Title"),
                _loc.Format("Msg_LargeFile", name, ToMegabytes(length.Value)), _loc.Get("Btn_Open"), _loc.Get("Btn_Cancel")))
        {
            return null;
        }

        try
        {
            return await _files.LoadAsync(path, encoding);
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
            var message = ex switch
            {
                FileTooLargeException => _loc.Format("Msg_FileTooLarge", name, ToMegabytes(FileLimits.MaxBytes)),
                BinaryFileException => _loc.Format("Msg_Binary", name),
                FileNotFoundException or DirectoryNotFoundException => _loc.Format("Msg_FileNotFound", path),
                UnauthorizedAccessException => _loc.Format("Msg_AccessDenied", path),
                _ => _loc.Format("Msg_ReadError", name, ex.Message),
            };
            await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), message);
            return null;
        }
    }

    private async Task<LoadedRichText?> LoadRichTextAsync(string path)
    {
        var length = _files.GetLength(path);
        if (length > FileLimits.WarnBytes && length <= FileLimits.MaxBytes &&
            !await _dialogs.ConfirmAsync(_loc.Get("Msg_LargeFile_Title"),
                _loc.Format("Msg_LargeFile", Path.GetFileName(path), ToMegabytes(length.Value)), _loc.Get("Btn_Open"), _loc.Get("Btn_Cancel")))
            return null;
        try
        {
            return await _files.LoadRichTextAsync(path);
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
            await _dialogs.ShowMessageAsync(_loc.Get("Msg_Error_Title"), _loc.Format("Msg_ReadError", Path.GetFileName(path), ex.Message));
            return null;
        }
    }

    private void ApplyLoaded(DocumentViewModel document, LoadedText loaded, string path)
    {
        document.LoadFrom(loaded, path);
        var encodingName = TextEncodings.GetDisplayName(loaded.Encoding);
        document.Notice =
            loaded.HadInvalidBytes ? new DocumentNotice(_loc.Format("Notice_InvalidBytes", encodingName), NoticeSeverity.Warning)
            : loaded.HasMixedLineEndings ? new DocumentNotice(_loc.Format("Notice_MixedLineEndings", LineEndings.GetDisplayName(document.LineEnding)), NoticeSeverity.Warning)
            : loaded.EncodingWasGuessed ? new DocumentNotice(_loc.Format("Notice_EncodingGuessed", encodingName), NoticeSeverity.Info)
            : null;
        RefreshCommands();
    }

    private async Task<bool> ConfirmDiscardOrSaveAsync(DocumentViewModel document)
    {
        ActiveDocument = document;
        return await _dialogs.ConfirmSaveChangesAsync(document.DisplayName) switch
        {
            SaveChangesChoice.Save => await SaveDocumentAsync(document, saveAs: false),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    private DocumentViewModel? FindOpenDocument(string path) =>
        Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));

    private void ShowFind(bool replace)
    {
        IsSettingsOpen = false;
        ActiveDocument?.Find.Show(replace);
    }

    private void CycleTab(int step)
    {
        if (Documents.Count > 1 && ActiveDocument is { } current)
        {
            var index = (Documents.IndexOf(current) + step + Documents.Count) % Documents.Count;
            ActiveDocument = Documents[index];
        }
    }

    private static string GetBaseName(DocumentViewModel document) =>
        document.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : document.DisplayName;

    private static string ToMegabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#");

    /// <summary>Re-evaluates which commands are available after the active document or its state changed.</summary>
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(HasFile));
        foreach (var command in new IRelayCommand[]
                 {
                     SaveCommand, SaveAsCommand, CloseTabCommand, ReopenClosedCommand, UndoCommand, RedoCommand, CutCommand, CopyCommand,
                     PasteCommand, SelectAllCommand, FindCommand, ReplaceCommand, GoToLineCommand, DuplicateLineCommand, DeleteLineCommand,
                     MoveLineUpCommand, MoveLineDownCommand, UppercaseCommand, LowercaseCommand, TrimTrailingWhitespaceCommand,
                     IndentCommand, OutdentCommand, IndentationToSpacesCommand, IndentationToTabsCommand, ZoomInCommand, ZoomOutCommand,
                     ResetZoomCommand, SetKindCommand, ToggleFormattingCommand, SetViewModeCommand, ToggleOutlineCommand, SetEncodingCommand,
                     ReopenWithEncodingCommand, SetLineEndingCommand, ToggleOverwriteCommand, BoldCommand, ItalicCommand, UnderlineCommand, StrikethroughCommand, InlineCodeCommand,
                     HeadingCommand, QuoteCommand, BulletListCommand, NumberedListCommand, TaskListCommand, CodeBlockCommand,
                     HorizontalRuleCommand, LinkCommand, ImageCommand, ExportHtmlCommand, ExportPdfCommand, PrintCommand, CopyHtmlCommand,
                 })
        {
            command.NotifyCanExecuteChanged();
        }
    }
}
