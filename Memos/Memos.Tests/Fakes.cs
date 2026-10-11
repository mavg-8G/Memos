using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;

namespace Memos.Tests;

/// <summary>A temporary folder that is deleted when the test ends.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MemosTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class FakeLocalizer : ILocalizer
{
    public string Get(string key) => key;

    public string Format(string key, params object?[] args) => $"{key}({string.Join(",", args)})";
}

public sealed class FakeDialogs : IDialogService
{
    public SaveChangesChoice SaveChangesAnswer { get; set; } = SaveChangesChoice.Cancel;

    public bool ConfirmAnswer { get; set; }

    public int? GoToLineAnswer { get; set; }

    public LinkInput? LinkAnswer { get; set; }

    public List<string> Messages { get; } = [];

    public List<string> Confirmations { get; } = [];

    public int SaveChangesPrompts { get; private set; }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText)
    {
        Confirmations.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<SaveChangesChoice> ConfirmSaveChangesAsync(string documentName)
    {
        SaveChangesPrompts++;
        return Task.FromResult(SaveChangesAnswer);
    }

    public Task<int?> PromptGoToLineAsync(int currentLine, int lineCount) => Task.FromResult(GoToLineAnswer);

    public Task<LinkInput?> PromptLinkAsync(string initialText) => Task.FromResult(LinkAnswer);
}

public sealed class FakePickers : IFilePickerService
{
    public IReadOnlyList<string> OpenAnswer { get; set; } = [];

    public string? SaveAnswer { get; set; }

    public string? ExportAnswer { get; set; }

    public string? ImageAnswer { get; set; }

    public Task<IReadOnlyList<string>> PickOpenFilesAsync() => Task.FromResult(OpenAnswer);

    public Task<string?> PickSaveFileAsync(string suggestedName, DocumentKind kind, string? suggestedFolder) => Task.FromResult(SaveAnswer);

    public Task<string?> PickExportFileAsync(string suggestedName, ExportFormat format, string? suggestedFolder) => Task.FromResult(ExportAnswer);

    public Task<string?> PickImageAsync(string? suggestedFolder) => Task.FromResult(ImageAnswer);
}

public sealed class FakeClipboard : IClipboardService
{
    public string? Html { get; private set; }

    public string? Text { get; private set; }

    public void SetHtml(string html, string plainText) => Html = html;

    public void SetText(string text) => Text = text;
}

public sealed class FakePdfExporter : IPdfExporter
{
    public string? Html { get; private set; }

    public string? OutputPath { get; private set; }

    public Task ExportAsync(string html, string? baseDirectory, string outputPath)
    {
        Html = html;
        OutputPath = outputPath;
        return Task.CompletedTask;
    }

    public string? PrintTitle { get; private set; }

    public Exception? PrintError { get; set; }

    public Task PrintAsync(string html, string? baseDirectory, string title)
    {
        if (PrintError is not null)
        {
            return Task.FromException(PrintError);
        }

        Html = html;
        PrintTitle = title;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Stands in for the editing control of a rich-text document. Like the real one, it rewrites the
/// RTF it is given in its own way and keeps the undo history to itself.
/// </summary>
public sealed class FakeRichEditor : IRichEditorSurface
{
    private readonly DocumentViewModel _document;

    public FakeRichEditor(DocumentViewModel document)
    {
        _document = document;
        document.Editor = this;
        LoadRichText(document.RichText);
    }

    /// <summary>How the control would write the same document.</summary>
    public static string Normalize(string rtf) => rtf.Replace(@"{\rtf1", @"{\rtf1\fbidis").TrimEnd() + "\r\n";

    public string Rtf { get; private set; } = "";

    public List<RichStyle> Toggled { get; } = [];

    public int Undos { get; private set; }

    public int Redos { get; private set; }

    public bool CanUndoRich { get; set; }

    public bool CanRedoRich { get; set; }

    public void LoadRichText(string rtf)
    {
        Rtf = Normalize(rtf);
        _document.OnEditorRichTextLoaded(Memos.Core.Helpers.RichTextCodec.ToPlainText(rtf), Rtf);
    }

    /// <summary>The user changed the document: new content, new formatting, or both.</summary>
    public void Edit(string rtf)
    {
        Rtf = Normalize(rtf);
        CanUndoRich = true;
        CanRedoRich = false;
        _document.OnEditorRichTextChanged(Memos.Core.Helpers.RichTextCodec.ToPlainText(rtf), Rtf);
    }

    public void ToggleRichStyle(RichStyle style) => Toggled.Add(style);

    public void UndoRich()
    {
        Undos++;
        CanUndoRich = false;
        CanRedoRich = true;
    }

    public void RedoRich()
    {
        Redos++;
        CanUndoRich = true;
        CanRedoRich = false;
    }

    public void ApplyEdit(TextEdit edit) => Edit(Memos.Core.Helpers.RichTextCodec.FromPlainText(edit.Apply(_document.Text)));

    public void Select(int start, int length)
    {
    }

    public void Focus()
    {
    }

    public void Cut()
    {
    }

    public void Copy()
    {
    }

    public void Paste()
    {
    }

    public void SelectAll()
    {
    }
}

/// <summary>A complete view-model graph over real services and a temporary data folder.</summary>
public sealed class TestApp : IDisposable
{
    public TestApp(TempDirectory? shared = null)
    {
        Temp = shared ?? new TempDirectory();
        _ownsTemp = shared is null;
        var paths = new AppPaths(Temp.File("data"));
        SettingsService = new JsonSettingsService(paths);
        Files = new FileService();
        Session = new SessionService(paths);
        Recent = new RecentFilesService(paths);
        ErrorLog = new ErrorLog(paths);
        Main = new MainViewModel(SettingsService, Files, Recent, Session, Dialogs, Pickers, Clipboard, Pdf,
            new SearchService(), new MarkdownService(), new FakeLocalizer(), ErrorLog);
    }

    private readonly bool _ownsTemp;

    public TempDirectory Temp { get; }

    public JsonSettingsService SettingsService { get; }

    public AppSettings Settings => SettingsService.Current;

    public FileService Files { get; }

    public SessionService Session { get; }

    public RecentFilesService Recent { get; }

    public ErrorLog ErrorLog { get; }

    public FakeDialogs Dialogs { get; } = new();

    public FakePickers Pickers { get; } = new();

    public FakeClipboard Clipboard { get; } = new();

    public FakePdfExporter Pdf { get; } = new();

    public MainViewModel Main { get; }

    public DocumentViewModel Active => Main.ActiveDocument!;

    public string WriteFile(string name, string content)
    {
        var path = Temp.File(name);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>Types text at the caret the way the view models see an edit.</summary>
    public void Type(string text) =>
        Active.ApplyEdit(new TextEdit(Active.SelectionStart, Active.SelectionLength, text, Active.SelectionStart + text.Length, 0));

    public void Dispose()
    {
        SettingsService.Dispose();
        if (_ownsTemp)
        {
            Temp.Dispose();
        }
    }
}

public static class TestHelpers
{
    public static DocumentViewModel NewDocument(DocumentKind kind = DocumentKind.Text, AppSettings? settings = null) =>
        new(new DocumentServices(new StubSettings(settings ?? new AppSettings()), new SearchService(), new MarkdownService(), new FakeLocalizer()), kind, "test");

    public static string Html(this MarkdownAnalysis analysis) => string.Concat(analysis.Blocks.Select(b => b.Html));

    private sealed class StubSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;

        public Task FlushAsync() => Task.CompletedTask;
    }
}
