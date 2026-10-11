using Memos.Core.Markdown;
using Memos.Core.Models;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Memos.Helpers;

/// <summary>
/// Text colours for Markdown syntax in the editor. Every theme has a light and a dark variant
/// chosen to stay readable over the window material.
/// </summary>
public sealed record SyntaxPalette(Color Default, Color Heading, Color Code, Color Link, Color Quote, Color Marker, Color Meta, Color SearchMatch)
{
    private static readonly UISettings SystemColors = new();

    private static readonly Color LightText = Color.FromArgb(0xE4, 0, 0, 0);
    private static readonly Color DarkText = Rgb(0xFF, 0xFF, 0xFF);
    private static readonly Color LightGray = Rgb(0x5F, 0x68, 0x72);
    private static readonly Color DarkGray = Rgb(0x9D, 0xA7, 0xB3);

    private static readonly SyntaxPalette DefaultLight = new(
        LightText, Rgb(0x05, 0x50, 0xAE), Rgb(0x8A, 0x3D, 0x9E), Rgb(0x09, 0x69, 0xDA),
        LightGray, Rgb(0xC2, 0x3B, 0x22), LightGray, Rgb(0xFF, 0xE0, 0x8A));

    private static readonly SyntaxPalette DefaultDark = new(
        DarkText, Rgb(0x79, 0xC0, 0xFF), Rgb(0xD2, 0xA8, 0xFF), Rgb(0x58, 0xA6, 0xFF),
        DarkGray, Rgb(0xFF, 0x9B, 0x8E), DarkGray, Rgb(0x6B, 0x55, 0x10));

    /// <summary>Raised (on a background thread) when the user changes the Windows accent colour.</summary>
    public static event EventHandler? SystemColorsChanged;

    static SyntaxPalette() => SystemColors.ColorValuesChanged += (_, _) => SystemColorsChanged?.Invoke(null, EventArgs.Empty);

    public static SyntaxPalette For(SyntaxTheme theme, bool dark) => theme switch
    {
        SyntaxTheme.Accent => FromAccent(dark),
        SyntaxTheme.Monochrome => Monochrome(dark),
        _ => dark ? DefaultDark : DefaultLight,
    };

    public Color Get(SyntaxColor color) => color switch
    {
        SyntaxColor.Heading => Heading,
        SyntaxColor.Code => Code,
        SyntaxColor.Link => Link,
        SyntaxColor.Quote => Quote,
        SyntaxColor.Marker => Marker,
        SyntaxColor.Meta => Meta,
        _ => Default,
    };

    /// <summary>
    /// Shades of the Windows accent colour: the darker ones on light backgrounds and the lighter ones
    /// on dark backgrounds, so contrast holds whatever accent the user picked.
    /// </summary>
    private static SyntaxPalette FromAccent(bool dark)
    {
        Color Accent(UIColorType type) => SystemColors.GetColorValue(type);

        if (dark)
        {
            var strong = Accent(UIColorType.AccentLight3);
            return new SyntaxPalette(
                DarkText, strong, Blend(strong, DarkGray, 0.55), Accent(UIColorType.AccentLight2),
                DarkGray, Accent(UIColorType.AccentLight1), DarkGray, Blend(Accent(UIColorType.AccentDark2), Rgb(0x20, 0x20, 0x20), 0.35));
        }

        var deep = Accent(UIColorType.AccentDark2);
        return new SyntaxPalette(
            LightText, deep, Blend(deep, LightGray, 0.55), Accent(UIColorType.AccentDark1),
            LightGray, Accent(UIColorType.Accent), LightGray, Blend(Accent(UIColorType.AccentLight3), Rgb(0xFF, 0xFF, 0xFF), 0.25));
    }

    /// <summary>No hues: structure is conveyed by weight, slant and shades of grey only.</summary>
    private static SyntaxPalette Monochrome(bool dark)
    {
        var text = dark ? DarkText : LightText;
        var gray = dark ? DarkGray : LightGray;
        return new SyntaxPalette(text, text, gray, text, gray, gray, gray, dark ? Rgb(0x55, 0x55, 0x55) : Rgb(0xD6, 0xD6, 0xD6));
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);

    /// <summary>Mixes two colours; <paramref name="amount"/> is how much of the second one.</summary>
    private static Color Blend(Color a, Color b, double amount) => Rgb(
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));
}
