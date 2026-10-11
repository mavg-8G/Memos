namespace Memos.Core.Models;

public enum DocumentKind
{
    Text,
    Markdown,
}

public enum LineEnding
{
    CrLf,
    Lf,
    Cr,
}

public enum TextEncodingId
{
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Windows1252,
}

public enum MarkdownViewMode
{
    Editor,
    Split,
    Preview,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum BackdropKind
{
    Mica,
    MicaAlt,
    Acrylic,
    None,
}

public enum PreviewStyle
{
    Default,
    Serif,
    Compact,
}

public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

public enum SaveChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Colours used to highlight Markdown syntax in the editor.</summary>
public enum SyntaxTheme
{
    Default,
    Accent,
    Monochrome,
}

public enum ExportFormat
{
    Html,
    Pdf,
}
