using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace Memos.Helpers;

/// <summary>XAML access to localized strings: <c>{h:Loc Key=Menu_File}</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Localizer.Instance?.Get(Key) ?? Key;
}

/// <summary>Functions for <c>x:Bind</c> expressions.</summary>
public static class Ui
{
    public const string TextDocumentGlyph = "";
    public const string MarkdownDocumentGlyph = "";

    public static string KindGlyph(DocumentKind kind) => kind == DocumentKind.Markdown ? MarkdownDocumentGlyph : TextDocumentGlyph;

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleUnless(bool value, bool hidden) => value && !hidden ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static string EncodingName(TextEncodingId id) => TextEncodings.GetDisplayName(id);

    public static string LineEndingName(LineEnding lineEnding) => LineEndings.GetDisplayName(lineEnding);

    public static string Percent(int value) => $"{value}%";

    public static string InsertMode(bool overwrite) => Localizer.Instance!.Get(overwrite ? "Status_Overwrite" : "Status_Insert");

    public static string Position(int line, int column) => Localizer.Instance!.Format("Status_Position", line, column);

    public static string Characters(int count) => Localizer.Instance!.Format("Status_Characters", count.ToString("N0"));

    public static string Words(int count) => Localizer.Instance!.Format("Status_Words", count.ToString("N0"));

    public static string ReadingTime(int minutes) => Localizer.Instance!.Format("Status_ReadingTime", minutes);

    /// <summary>Indentation of an outline entry according to its heading level.</summary>
    public static Thickness OutlineMargin(int level) => new((level - 1) * 12, 0, 0, 0);

    public static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

public sealed record FontInfo(string Name, bool IsMonospaced);

/// <summary>Installed font families, read once through GDI.</summary>
public static partial class FontEnumerator
{
    private static List<FontInfo>? _fonts;

    public static IReadOnlyList<FontInfo> GetFonts()
    {
        if (_fonts is not null)
        {
            return _fonts;
        }

        var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var dc = GetDC(0);
        try
        {
            var logFont = new LogFont { CharacterSet = 1 /* DEFAULT_CHARSET */ };
            EnumFontFamiliesExW(dc, ref logFont, (ref LogFont font, nint _, uint _, nint _) =>
            {
                var name = font.GetFaceName();
                if (name.Length > 0 && name[0] != '@')
                {
                    // Low two bits of the pitch: 1 means fixed pitch.
                    found[name] = (font.PitchAndFamily & 0x3) == 1;
                }

                return 1;
            }, 0, 0);
        }
        finally
        {
            _ = ReleaseDC(0, dc);
        }

        return _fonts = [.. found.OrderBy(f => f.Key, StringComparer.CurrentCultureIgnoreCase).Select(f => new FontInfo(f.Key, f.Value))];
    }

    private delegate int EnumFontProc(ref LogFont font, nint textMetric, uint fontType, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharacterSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;

        public readonly string GetFaceName() => FaceName ?? "";
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesExW(nint hdc, ref LogFont logFont, EnumFontProc callback, nint lParam, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hdc);
}
