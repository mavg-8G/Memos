using CommunityToolkit.Mvvm.ComponentModel;

namespace Memos.Core.Models;

/// <summary>
/// User preferences. Every property raises <c>PropertyChanged</c> so views can apply changes live,
/// and the settings service persists the object whenever something changes.
/// </summary>
public partial class AppSettings : ObservableObject
{
    public const int MinFontSize = 8;
    public const int MaxFontSize = 48;
    public const int MinZoom = 25;
    public const int MaxZoom = 500;

    // Appearance
    [ObservableProperty] public partial AppTheme Theme { get; set; } = AppTheme.System;
    [ObservableProperty] public partial BackdropKind Backdrop { get; set; } = BackdropKind.Mica;
    [ObservableProperty] public partial string FontFamily { get; set; } = "Cascadia Mono";
    [ObservableProperty] public partial double FontSize { get; set; } = 14;
    [ObservableProperty] public partial int DefaultZoom { get; set; } = 100;
    [ObservableProperty] public partial bool WordWrap { get; set; } = true;
    [ObservableProperty] public partial bool ShowStatusBar { get; set; } = true;

    // Editor
    [ObservableProperty] public partial TextEncodingId DefaultEncoding { get; set; } = TextEncodingId.Utf8;
    [ObservableProperty] public partial LineEnding DefaultLineEnding { get; set; } = LineEnding.CrLf;
    [ObservableProperty] public partial bool InsertSpaces { get; set; } = true;
    [ObservableProperty] public partial int TabSize { get; set; } = 4;
    [ObservableProperty] public partial bool ShowLineNumbers { get; set; } = true;
    [ObservableProperty] public partial bool AutoIndent { get; set; } = true;
    [ObservableProperty] public partial bool AutoSave { get; set; }

    // Markdown
    [ObservableProperty] public partial MarkdownViewMode MarkdownViewMode { get; set; } = MarkdownViewMode.Split;
    [ObservableProperty] public partial bool SyncScroll { get; set; } = true;
    [ObservableProperty] public partial PreviewStyle PreviewStyle { get; set; } = PreviewStyle.Default;
    [ObservableProperty] public partial double PreviewFontSize { get; set; } = 15;
    [ObservableProperty] public partial bool CodeHighlighting { get; set; } = true;
    [ObservableProperty] public partial bool MarkdownSyntaxHighlighting { get; set; } = true;
    [ObservableProperty] public partial bool AllowRemoteImages { get; set; }

    // General
    /// <summary>BCP-47 tag ("es-ES", "en-US") or empty to follow Windows.</summary>
    [ObservableProperty] public partial string Language { get; set; } = "";
    [ObservableProperty] public partial bool RestoreSession { get; set; }
    [ObservableProperty] public partial bool ShowRecentFiles { get; set; } = true;
    [ObservableProperty] public partial bool ConfirmCloseModified { get; set; } = true;
    [ObservableProperty] public partial DocumentKind StartupDocument { get; set; } = DocumentKind.Text;

    // Window placement (not shown in the settings page).
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Clamps values that could have been edited by hand in the JSON file.</summary>
    public void Normalize()
    {
        FontSize = Math.Clamp(FontSize, MinFontSize, MaxFontSize);
        PreviewFontSize = Math.Clamp(PreviewFontSize, MinFontSize, MaxFontSize);
        DefaultZoom = Math.Clamp(DefaultZoom, MinZoom, MaxZoom);
        TabSize = Math.Clamp(TabSize, 1, 16);
        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            FontFamily = "Cascadia Mono";
        }

        Language ??= "";
    }
}
