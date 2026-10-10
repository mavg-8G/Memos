namespace Memos.Core.Models;

/// <summary>Identity of a file on disk at the moment it was read or written; used to detect external changes.</summary>
public readonly record struct FileStamp(DateTime LastWriteUtc, long Length);

/// <summary>Result of reading a text file. <see cref="Text"/> always uses '\n' as its only line separator.</summary>
public sealed record LoadedText(
    string Text,
    TextEncodingId Encoding,
    LineEnding? LineEnding,
    bool HasMixedLineEndings,
    bool HadInvalidBytes,
    bool EncodingWasGuessed,
    FileStamp Stamp);

/// <summary>A replacement of <c>Length</c> characters at <c>Start</c>, plus the selection to set afterwards.</summary>
public readonly record struct TextEdit(int Start, int Length, string NewText, int SelectionStart, int SelectionLength)
{
    public string Apply(string text) =>
        string.Concat(text.AsSpan(0, Start), NewText, text.AsSpan(Start + Length));
}

public readonly record struct TextMatch(int Start, int Length)
{
    public int End => Start + Length;
}

public sealed record SearchOptions(bool MatchCase = false, bool WholeWord = false, bool UseRegex = false);

public sealed record DocumentNotice(string Message, NoticeSeverity Severity, string? ActionLabel = null, Func<Task>? Action = null);

public sealed record LinkInput(string Text, string Url);

public sealed record SessionDocument
{
    public Guid Id { get; set; }
    public string? FilePath { get; set; }
    public string DisplayName { get; set; } = "";
    public DocumentKind Kind { get; set; }
    public bool HasBackup { get; set; }
    public TextEncodingId Encoding { get; set; }
    public LineEnding LineEnding { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
    public MarkdownViewMode ViewMode { get; set; }
}

public sealed record SessionState
{
    public List<SessionDocument> Documents { get; set; } = [];
    public int ActiveIndex { get; set; }
}

/// <summary>What is needed to bring back a tab the user closed.</summary>
public sealed record ClosedDocument(string? FilePath, string DisplayName, DocumentKind Kind, string? Text, int SelectionStart);
