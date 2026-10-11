using System.Reflection;
using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;
using Memos.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Memos.Views;

/// <summary>Preferences, grouped by category. Every change applies immediately and is saved by the settings service.</summary>
public sealed partial class SettingsPage : UserControl
{
    private readonly ILocalizer _loc;
    private readonly string _initialLanguage;

    public SettingsPage(MainViewModel viewModel, ILocalizer localizer)
    {
        ViewModel = viewModel;
        _loc = localizer;
        _initialLanguage = viewModel.Settings.Language;
        InitializeComponent();

        BindCombo(ThemeCombo, [AppTheme.System, AppTheme.Light, AppTheme.Dark], "Theme", () => Settings.Theme, v => Settings.Theme = v);
        BindCombo(BackdropCombo, [BackdropKind.Mica, BackdropKind.MicaAlt, BackdropKind.Acrylic, BackdropKind.None], "Backdrop", () => Settings.Backdrop, v => Settings.Backdrop = v);
        BindCombo(EncodingCombo, TextEncodings.All, null, () => Settings.DefaultEncoding, v => Settings.DefaultEncoding = v, TextEncodings.GetDisplayName);
        BindCombo(LineEndingCombo, [LineEnding.CrLf, LineEnding.Lf, LineEnding.Cr], "LineEnding", () => Settings.DefaultLineEnding, v => Settings.DefaultLineEnding = v);
        BindCombo(ViewModeCombo, [MarkdownViewMode.Editor, MarkdownViewMode.Split, MarkdownViewMode.Preview], "ViewMode", () => Settings.MarkdownViewMode, v => Settings.MarkdownViewMode = v);
        BindCombo(PreviewStyleCombo, [PreviewStyle.Default, PreviewStyle.Serif, PreviewStyle.Compact], "PreviewStyle", () => Settings.PreviewStyle, v => Settings.PreviewStyle = v);
        BindCombo(SyntaxThemeCombo, [SyntaxTheme.Default, SyntaxTheme.Accent, SyntaxTheme.Monochrome], "SyntaxTheme", () => Settings.SyntaxTheme, v => Settings.SyntaxTheme = v);
        BindCombo(StartupCombo, [DocumentKind.Text, DocumentKind.Markdown], "Startup", () => Settings.StartupDocument, v => Settings.StartupDocument = v);
        BindCombo(LanguageCombo, ["", "es-ES", "en-US"], null, () => Settings.Language, v =>
        {
            Settings.Language = v;
            RestartBar.IsOpen = v != _initialLanguage;
        }, tag => tag switch { "es-ES" => "Español", "en-US" => "English", _ => _loc.Get("Language_System") });

        BindNumber(FontSizeBox, AppSettings.MinFontSize, AppSettings.MaxFontSize, () => Settings.FontSize, v => Settings.FontSize = v);
        BindNumber(PreviewFontSizeBox, AppSettings.MinFontSize, AppSettings.MaxFontSize, () => Settings.PreviewFontSize, v => Settings.PreviewFontSize = v);
        BindNumber(DefaultZoomBox, AppSettings.MinZoom, AppSettings.MaxZoom, () => Settings.DefaultZoom, v => Settings.DefaultZoom = (int)v);
        BindNumber(TabSizeBox, 1, 16, () => Settings.TabSize, v => Settings.TabSize = (int)v);

        var fonts = FontEnumerator.GetFonts();
        MonospacedOnlyCheck.IsChecked = fonts.FirstOrDefault(f => string.Equals(f.Name, Settings.FontFamily, StringComparison.OrdinalIgnoreCase))?.IsMonospaced ?? true;
        FillFonts();
        MonospacedOnlyCheck.Checked += (_, _) => FillFonts();
        MonospacedOnlyCheck.Unchecked += (_, _) => FillFonts();
        FontCombo.SelectionChanged += (_, _) =>
        {
            if (FontCombo.SelectedItem is string font)
            {
                Settings.FontFamily = font;
            }
        };

        UpdateErrorLogCard();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HasErrorLog))
            {
                UpdateErrorLogCard();
            }
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = _loc.Format("About_Version", version?.ToString(3) ?? "1.0.0");
    }

    public MainViewModel ViewModel { get; }

    public AppSettings Settings => ViewModel.Settings;

    /// <summary>Says where the log is when there is one, and that all is well when there is not.</summary>
    private void UpdateErrorLogCard() => ErrorLogCard.Description = ViewModel.HasErrorLog
        ? _loc.Format("ErrorLog_Desc", ViewModel.ErrorLogPath)
        : _loc.Get("ErrorLog_Empty");

    public void FocusFirst() => DispatcherQueue.TryEnqueue(() => BackButton.Focus(FocusState.Programmatic));

    private void FillFonts()
    {
        var monospacedOnly = MonospacedOnlyCheck.IsChecked == true;
        var names = FontEnumerator.GetFonts().Where(f => !monospacedOnly || f.IsMonospaced).Select(f => f.Name).ToList();
        var current = Settings.FontFamily;

        // A proportional font stays selectable while the filter is on if it is the one in use.
        if (!names.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, current);
        }

        FontCombo.ItemsSource = names;
        FontCombo.SelectedItem = names.First(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Fills a combo with the values of a setting and keeps the setting in step with the selection.</summary>
    private void BindCombo<T>(ComboBox combo, IEnumerable<T> values, string? resourcePrefix, Func<T> get, Action<T> set, Func<T, string>? label = null)
    {
        foreach (var value in values)
        {
            var text = label is not null ? label(value) : _loc.Get($"{resourcePrefix}_{value}");
            var item = new ComboBoxItem { Content = text, Tag = value };
            combo.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(value, get()))
            {
                combo.SelectedItem = item;
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T selected })
            {
                set(selected);
            }
        };
    }

    private static void BindNumber(NumberBox box, double minimum, double maximum, Func<double> get, Action<double> set)
    {
        box.Minimum = minimum;
        box.Maximum = maximum;
        box.Value = get();
        box.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue))
            {
                box.Value = get(); // the field was cleared: put the current value back
            }
            else
            {
                set(Math.Clamp(Math.Round(e.NewValue), minimum, maximum));
            }
        };
    }

    private async void OnDefaultAppsClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
}
