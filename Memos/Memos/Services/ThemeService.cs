using System.ComponentModel;
using Memos.Core.Models;
using Memos.Core.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Memos.Services;

/// <summary>Applies the theme and the window material, and keeps them in step with the settings.</summary>
public sealed class ThemeService
{
    private readonly AppSettings _settings;
    private readonly WindowContext _context;
    private bool _hasBackdrop = true;

    public ThemeService(ISettingsService settings, WindowContext context)
    {
        _settings = settings.Current;
        _context = context;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    public void Apply()
    {
        _context.Root.ActualThemeChanged += (_, _) => ApplyBackground();
        ApplyTheme();
        ApplyBackdrop();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_context.Window is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
                ApplyTheme();
                break;
            case nameof(AppSettings.Backdrop):
                ApplyBackdrop();
                break;
        }
    }

    private void ApplyTheme()
    {
        var theme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _context.Root.RequestedTheme = theme;

        // Caption buttons follow the app theme rather than the system one.
        _context.Window!.AppWindow.TitleBar.PreferredTheme = _settings.Theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void ApplyBackdrop()
    {
        var window = _context.Window!;
        var root = (Microsoft.UI.Xaml.Controls.Panel)_context.Root;
        SystemBackdrop? backdrop = _settings.Backdrop switch
        {
            BackdropKind.Mica when MicaController.IsSupported() => new MicaBackdrop(),
            BackdropKind.MicaAlt when MicaController.IsSupported() => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            BackdropKind.Acrylic when DesktopAcrylicController.IsSupported() => new DesktopAcrylicBackdrop(),
            _ => null,
        };

        window.SystemBackdrop = backdrop;

        _hasBackdrop = backdrop is not null;
        ApplyBackground();
    }

    /// <summary>Without a material the window needs an opaque background of its own.</summary>
    private void ApplyBackground()
    {
        var root = (Microsoft.UI.Xaml.Controls.Panel)_context.Root;
        var color = _hasBackdrop
            ? Microsoft.UI.Colors.Transparent
            : root.ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
                : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        root.Background = new SolidColorBrush(color);
    }
}
