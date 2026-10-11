using Memos.Core.Helpers;
using Memos.Core.Markdown;
using Memos.Core.Models;

namespace Memos.Core.Services;

/// <summary>Localized strings by resource key.</summary>
public interface ILocalizer
{
    string Get(string key);

    string Format(string key, params object?[] args);
}

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    /// <summary>Asks a yes/no question. Returns true when the primary button is chosen.</summary>
    Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText);

    Task<SaveChangesChoice> ConfirmSaveChangesAsync(string documentName);

    /// <summary>Returns a one-based line number, or null if cancelled.</summary>
    Task<int?> PromptGoToLineAsync(int currentLine, int lineCount);

    Task<LinkInput?> PromptLinkAsync(string initialText);
}

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> PickOpenFilesAsync();

    Task<string?> PickSaveFileAsync(string suggestedName, DocumentKind kind, string? suggestedFolder);

    Task<string?> PickExportFileAsync(string suggestedName, ExportFormat format, string? suggestedFolder);

    Task<string?> PickImageAsync(string? suggestedFolder);
}

public interface IClipboardService
{
    /// <summary>Puts HTML on the clipboard together with a plain-text version.</summary>
    void SetHtml(string html, string plainText);

    void SetText(string text);
}

public interface IPdfExporter
{
    /// <summary>Prints an HTML document to a PDF file. Relative resources resolve against <paramref name="baseDirectory"/>.</summary>
    Task ExportAsync(string html, string? baseDirectory, string outputPath);
}

/// <summary>Printing through the print dialog of Windows. Both calls return once the dialog is showing.</summary>
public interface IPrintService
{
    /// <summary>Prints text with its character styles; paragraphs end with '\n'.</summary>
    Task PrintTextAsync(string title, IReadOnlyList<RichTextRun> runs, bool monospaced);

    /// <summary>Prints a Markdown document. Relative images resolve against <paramref name="baseDirectory"/>.</summary>
    Task PrintMarkdownAsync(string title, IReadOnlyList<PrintBlock> blocks, string? baseDirectory);
}

/// <summary>
/// The editing control as the view models see it. Offsets are UTF-16 positions in '\n'-separated text.
/// The implementation reports user edits back through <c>DocumentViewModel.OnEditorTextChanged</c>.
/// </summary>
public interface IEditorSurface
{
    /// <summary>Replaces a range and sets the selection, reporting the resulting text before returning.</summary>
    void ApplyEdit(TextEdit edit);

    void Select(int start, int length);

    void Focus();

    void Cut();

    void Copy();

    void Paste();

    void SelectAll();
}

public interface IRichEditorSurface : IEditorSurface
{
    bool CanUndoRich { get; }
    bool CanRedoRich { get; }
    void LoadRichText(string rtf);
    void ToggleRichStyle(RichStyle style);
    void UndoRich();
    void RedoRich();
}

public enum RichStyle { Bold, Italic, Underline, Strikethrough }
