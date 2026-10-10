using System.ComponentModel;
using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;
using Memos.Helpers;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Memos.Views;

/// <summary>
/// The text surface of a document: a native RichEditBox used as a plain-text editor, plus a line
/// number gutter. The control owns nothing but presentation; text, undo history and commands live
/// in the <see cref="DocumentViewModel"/> it is attached to.
/// </summary>
public sealed partial class EditorControl : UserControl, IEditorSurface
{
    /// <summary>At most this many search matches get a background highlight.</summary>
    private const int MaxHighlightedMatches = 3000;

    private readonly List<TextBlock> _gutterLabels = [];
    private readonly List<(int Start, int End)> _highlighted = [];
    private DocumentViewModel? _document;
    private AppSettings? _settings;
    private ScrollViewer? _scroller;
    private string _lastText = "";
    private bool _hasFinalParagraphMark;
    private bool _textDirty;
    private bool _updating;
    private bool _hasStyling;
    private IReadOnlyList<SyntaxRun> _runs = [];

    public EditorControl()
    {
        InitializeComponent();

        // Undo is handled by the view model so that styling changes never end up in the history.
        Box.Document.UndoLimit = 0;

        Box.TextChanging += (_, e) => _textDirty |= e.IsContentChanging;
        Box.TextChanged += (_, _) => SyncFromControl();
        Box.SelectionChanged += (_, _) => OnSelectionChanged();
        Box.Paste += OnPaste;
        Box.PreviewKeyDown += OnPreviewKeyDown;
        Box.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheel), handledEventsToo: true);
        Box.Loaded += OnBoxLoaded;
        Box.SizeChanged += (_, _) => UpdateGutter();
        ActualThemeChanged += (_, _) => OnThemeChanged();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Raised when the visible part of the text changes (scrolling, resizing, editing).</summary>
    public event EventHandler? ViewChanged;

    // ---- Attachment ---------------------------------------------------------------------------------

    public void Attach(DocumentViewModel document, AppSettings settings)
    {
        _document = document;
        _settings = settings;
        settings.PropertyChanged += OnSettingsChanged;
        document.PropertyChanged += OnDocumentPropertyChanged;

        ApplyAppearance();
        SetWholeText(document.Text);
        SetSelection(document.SelectionStart, document.SelectionLength);
        document.Editor = this;
    }

    public void Detach()
    {
        if (_document is null)
        {
            return;
        }

        if (ReferenceEquals(_document.Editor, this))
        {
            _document.Editor = null;
        }

        _document.PropertyChanged -= OnDocumentPropertyChanged;
        _settings!.PropertyChanged -= OnSettingsChanged;
        _document = null;
    }

    // ---- IEditorSurface -----------------------------------------------------------------------------

    public void ApplyEdit(TextEdit edit)
    {
        var document = Box.Document;
        _updating = true;
        try
        {
            document.BatchDisplayUpdates();
            var range = document.GetRange(edit.Start, edit.Start + edit.Length);
            range.SetText(TextSetOptions.None, ToControl(edit.NewText));
            document.Selection.SetRange(edit.SelectionStart, edit.SelectionStart + edit.SelectionLength);
            document.ApplyDisplayUpdates();
        }
        finally
        {
            _updating = false;
        }

        _textDirty = true;
        SyncFromControl();
        document.Selection.ScrollIntoView(PointOptions.None);
    }

    public void Select(int start, int length)
    {
        SetSelection(start, length);
        Box.Document.Selection.ScrollIntoView(PointOptions.None);
    }

    void IEditorSurface.Focus() => Box.Focus(FocusState.Programmatic);

    public void Cut()
    {
        Box.Focus(FocusState.Programmatic);
        Box.Document.Selection.Cut();
    }

    public void Copy() => Box.Document.Selection.Copy();

    public void Paste()
    {
        Box.Focus(FocusState.Programmatic);
        _ = PasteTextAsync();
    }

    public void SelectAll()
    {
        Box.Focus(FocusState.Programmatic);
        Box.Document.Selection.SetRange(0, TextConstants.MaxUnitCount);
    }

    // ---- Styling ------------------------------------------------------------------------------------

    /// <summary>
    /// Colours Markdown syntax. With a dirty range only that part is redone; the rest keeps the
    /// formatting it already carries.
    /// </summary>
    public void ApplySyntaxRuns(IReadOnlyList<SyntaxRun> runs, (int Start, int End)? dirtyRange)
    {
        _runs = runs;
        var length = _lastText.Length;
        var (start, end) = dirtyRange ?? (0, length);
        end = Math.Min(end, length);
        if (end <= start || (runs.Count == 0 && !_hasStyling))
        {
            return;
        }

        _hasStyling = true;
        var palette = SyntaxPalette.For(ActualTheme);
        var document = Box.Document;
        _updating = true;
        try
        {
            document.BatchDisplayUpdates();
            var plain = document.GetRange(start, end).CharacterFormat;
            plain.ForegroundColor = palette.Default;
            plain.Bold = FormatEffect.Off;
            plain.Italic = FormatEffect.Off;
            plain.Strikethrough = FormatEffect.Off;

            for (var i = FirstRunEndingAfter(runs, start); i < runs.Count && runs[i].Start < end; i++)
            {
                var run = runs[i];
                var format = document.GetRange(Math.Max(run.Start, start), Math.Min(run.End, end)).CharacterFormat;
                if (run.Color != SyntaxColor.None)
                {
                    format.ForegroundColor = palette.Get(run.Color);
                }

                if (run.Flags.HasFlag(SyntaxFlags.Bold))
                {
                    format.Bold = FormatEffect.On;
                }

                if (run.Flags.HasFlag(SyntaxFlags.Italic))
                {
                    format.Italic = FormatEffect.On;
                }

                if (run.Flags.HasFlag(SyntaxFlags.Strike))
                {
                    format.Strikethrough = FormatEffect.On;
                }
            }

            document.ApplyDisplayUpdates();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Marks every search match with a background colour and clears the previous marks.</summary>
    public void ApplySearchHighlights(IReadOnlyList<TextMatch> matches)
    {
        if (matches.Count == 0 && _highlighted.Count == 0)
        {
            return;
        }

        var document = Box.Document;
        var length = _lastText.Length;
        var color = SyntaxPalette.For(ActualTheme).SearchMatch;
        _updating = true;
        try
        {
            document.BatchDisplayUpdates();
            foreach (var (start, end) in _highlighted)
            {
                if (start < length)
                {
                    document.GetRange(start, Math.Min(end, length)).CharacterFormat.BackgroundColor = Colors.Transparent;
                }
            }

            _highlighted.Clear();
            foreach (var match in matches.Take(MaxHighlightedMatches))
            {
                if (match.End <= length)
                {
                    document.GetRange(match.Start, match.End).CharacterFormat.BackgroundColor = color;
                    _highlighted.Add((match.Start, match.End));
                }
            }

            document.ApplyDisplayUpdates();
        }
        finally
        {
            _updating = false;
        }
    }

    // ---- Scrolling ----------------------------------------------------------------------------------

    /// <summary>
    /// Zero-based line at the top of the viewport, with a fractional part for how far it is scrolled.
    /// </summary>
    public double GetFirstVisibleLine()
    {
        if (_document is null || _scroller is null || _scroller.VerticalOffset <= 0)
        {
            return 0;
        }

        var line = FindFirstVisibleLine();
        var top = GetLineTop(line);
        var next = line + 1 < _document.LineCount ? GetLineTop(line + 1) : top + GetLineHeight();
        return line + (next > top ? Math.Clamp(-top / (next - top), 0, 1) : 0);
    }

    /// <summary>True when the text is scrolled all the way down.</summary>
    public bool IsScrolledToEnd => _scroller is not null && _scroller.ScrollableHeight > 0 && _scroller.VerticalOffset >= _scroller.ScrollableHeight - 1;

    // ---- Control events -----------------------------------------------------------------------------

    private void OnBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroller is null && Ui.FindDescendant<ScrollViewer>(Box) is { } scroller)
        {
            _scroller = scroller;
            scroller.ViewChanged += (_, _) =>
            {
                UpdateGutter();
                ViewChanged?.Invoke(this, EventArgs.Empty);
            };
        }

        UpdateGutter();
    }

    /// <summary>Reads the control's text if it changed and hands it to the view model.</summary>
    private void SyncFromControl()
    {
        if (_document is null || _updating || !_textDirty)
        {
            return;
        }

        _textDirty = false;
        var text = ReadText();
        if (!string.Equals(text, _lastText, StringComparison.Ordinal))
        {
            _lastText = text;
            _document.OnEditorTextChanged(text);
            UpdateGutter();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        ReportSelection();
    }

    private void OnSelectionChanged()
    {
        if (_document is null || _updating)
        {
            return;
        }

        // The caret usually moves because of typing: make sure the text is current first.
        if (_textDirty)
        {
            SyncFromControl();
        }
        else
        {
            ReportSelection();
        }
    }

    private void ReportSelection()
    {
        var selection = Box.Document.Selection;
        var start = Math.Min(selection.StartPosition, selection.EndPosition);
        var end = Math.Max(selection.StartPosition, selection.EndPosition);
        _document?.OnEditorSelectionChanged(start, Math.Min(end, _lastText.Length) - Math.Min(start, _lastText.Length));
    }

    private void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        // Always paste as plain text: formatting from other apps must never enter the document.
        e.Handled = true;
        _ = PasteTextAsync();
    }

    private async Task PasteTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (_document is null || !content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = LineEndings.Normalize(await content.GetTextAsync()).Replace("\0", "");
            if (text.Length > 0 && _document is { } document)
            {
                document.ApplyEdit(new TextEdit(document.SelectionStart, document.SelectionLength, text, document.SelectionStart + text.Length, 0));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // The clipboard is held by another program; nothing to paste right now.
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var control = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);

        if (e.Key == VirtualKey.Tab && !control && !alt)
        {
            _document.HandleTab(shift);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && !control && !alt)
        {
            // Shift+Enter would insert a vertical tab, which is not a line break in a text file.
            if (_document.HandleEnter())
            {
                e.Handled = true;
            }
            else if (shift)
            {
                _document.ApplyEdit(new TextEdit(_document.SelectionStart, _document.SelectionLength, "\n", _document.SelectionStart + 1, 0));
                e.Handled = true;
            }
        }
        else if (control && !alt && !IsNativeControlShortcut(e.Key))
        {
            // RichEditBox has rich-text shortcuts (alignment, line spacing, lists…) that make no
            // sense in a plain-text editor. Application shortcuts were already handled by the window.
            e.Handled = true;
        }
    }

    private static bool IsNativeControlShortcut(VirtualKey key) => key is
        VirtualKey.C or VirtualKey.X or VirtualKey.V or VirtualKey.A or
        VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
        VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or
        VirtualKey.Back or VirtualKey.Delete or VirtualKey.Insert or
        VirtualKey.Control or VirtualKey.Shift or VirtualKey.LeftControl or VirtualKey.RightControl;

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_document is null || !e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            return;
        }

        var delta = e.GetCurrentPoint(Box).Properties.MouseWheelDelta;
        if (delta > 0)
        {
            _document.ZoomIn();
        }
        else if (delta < 0)
        {
            _document.ZoomOut();
        }

        e.Handled = true;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => _document?.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => _document?.Redo();

    private void OnCutClick(object sender, RoutedEventArgs e) => Cut();

    private void OnCopyClick(object sender, RoutedEventArgs e) => Copy();

    private void OnPasteClick(object sender, RoutedEventArgs e) => Paste();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SelectAll();

    // ---- Appearance ---------------------------------------------------------------------------------

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.FontFamily) or nameof(AppSettings.FontSize) or nameof(AppSettings.WordWrap)
            or nameof(AppSettings.ShowLineNumbers) or nameof(AppSettings.TabSize))
        {
            ApplyAppearance();
        }
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.ZoomPercent))
        {
            ApplyAppearance();
        }
    }

    private void ApplyAppearance()
    {
        if (_document is null || _settings is null)
        {
            return;
        }

        var size = Math.Max(4, _settings.FontSize * _document.ZoomPercent / 100.0);
        Box.FontFamily = new FontFamily(_settings.FontFamily + ", Cascadia Mono, Consolas");
        Box.FontSize = size;
        Box.TextWrapping = _settings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

        // Tab stops are measured in points; a space of a monospaced font is about 0.55 em wide.
        Box.Document.DefaultTabStop = (float)(_settings.TabSize * MeasureSpaceWidth(size) * 72.0 / 96.0);
        UpdateGutter();
    }

    private double MeasureSpaceWidth(double fontSize)
    {
        var probe = new TextBlock { Text = "0000000000", FontFamily = Box.FontFamily, FontSize = fontSize };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width > 0 ? probe.DesiredSize.Width / 10 : fontSize * 0.55;
    }

    private void OnThemeChanged()
    {
        // Colours written into the text are fixed values, so they are rewritten for the new theme.
        if (_hasStyling)
        {
            ApplySyntaxRuns(_runs, null);
        }

        if (_document?.Find is { IsVisible: true } find)
        {
            ApplySearchHighlights(find.Matches);
        }

        UpdateGutter();
    }

    // ---- Text plumbing ------------------------------------------------------------------------------

    private static string ToControl(string text) => text.Replace('\n', '\r');

    private string ReadText()
    {
        Box.Document.GetText(TextGetOptions.None, out var raw);
        if (_hasFinalParagraphMark && raw.EndsWith('\r'))
        {
            raw = raw[..^1];
        }

        return raw.Replace('\r', '\n');
    }

    private void SetWholeText(string text)
    {
        var document = Box.Document;
        _updating = true;
        try
        {
            document.SetText(TextSetOptions.None, ToControl(text));

            // RichEdit always keeps a final paragraph mark. Whether GetText reports it is detected
            // here once, so the text read back is exactly the text that was set.
            document.GetText(TextGetOptions.None, out var raw);
            _hasFinalParagraphMark = raw.Length == text.Length + 1 && raw.EndsWith('\r');
        }
        finally
        {
            _updating = false;
        }

        _lastText = ReadText();
        _highlighted.Clear();
        _hasStyling = false;
        _textDirty = false;
        if (!string.Equals(_lastText, text, StringComparison.Ordinal))
        {
            // The control normalised something (an exotic control character); keep both sides equal.
            _document?.OnEditorTextChanged(_lastText);
        }
    }

    private void SetSelection(int start, int length)
    {
        _updating = true;
        try
        {
            Box.Document.Selection.SetRange(start, start + length);
        }
        finally
        {
            _updating = false;
        }

        ReportSelection();
    }

    private static int FirstRunEndingAfter(IReadOnlyList<SyntaxRun> runs, int offset)
    {
        int low = 0, high = runs.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (runs[mid].End <= offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // ---- Line numbers -------------------------------------------------------------------------------

    /// <summary>
    /// Where the text area starts inside the control. RichEdit reports positions relative to it,
    /// that is, after the padding of the inner scroll viewer.
    /// </summary>
    private double GetTextOriginY() => _scroller is null
        ? Box.Padding.Top
        : _scroller.TransformToVisual(Box).TransformPoint(new Point(0, 0)).Y + _scroller.Padding.Top;

    /// <summary>Vertical position of a line relative to the top of the text area, in view pixels.</summary>
    private double GetLineTop(int line)
    {
        var offset = _document!.GetLineStart(line);
        Box.Document.GetRange(offset, offset).GetRect(PointOptions.ClientCoordinates, out var rect, out _);

        // The rectangle is in document coordinates: it does not move when the view scrolls.
        return rect.Top - (_scroller?.VerticalOffset ?? 0);
    }

    private double GetLineHeight()
    {
        Box.Document.GetRange(0, 0).GetRect(PointOptions.ClientCoordinates, out var rect, out _);
        return rect.Height > 0 ? rect.Height : Box.FontSize * 1.3;
    }

    /// <summary>Last line whose top is at or above the top edge of the viewport.</summary>
    private int FindFirstVisibleLine()
    {
        int low = 0, high = _document!.LineCount - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (GetLineTop(mid) <= 1)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private void UpdateGutter()
    {
        if (_document is null || _settings is null || !_settings.ShowLineNumbers || Box.ActualHeight <= 0)
        {
            Gutter.Visibility = Visibility.Collapsed;
            return;
        }

        Gutter.Visibility = Visibility.Visible;
        var lineCount = _document.LineCount;
        var fontSize = Math.Max(4, Box.FontSize * 0.85);
        var digits = Math.Max(2, lineCount.ToString().Length);
        var width = Math.Ceiling(digits * fontSize * 0.62) + 20;
        Gutter.Width = width;
        Gutter.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, Box.ActualHeight) };

        var lineHeight = GetLineHeight();
        var origin = GetTextOriginY();
        var used = 0;
        for (var line = FindFirstVisibleLine(); line < lineCount; line++)
        {
            var top = GetLineTop(line) + origin;
            if (top > Box.ActualHeight)
            {
                break;
            }

            if (used == _gutterLabels.Count)
            {
                var created = new TextBlock { Style = (Style)Resources["GutterLabelStyle"] };
                _gutterLabels.Add(created);
                Gutter.Children.Add(created);
            }

            var label = _gutterLabels[used++];
            label.Text = (line + 1).ToString();
            label.FontFamily = Box.FontFamily;
            label.FontSize = fontSize;
            label.Width = width - 10;
            label.Visibility = Visibility.Visible;
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, top + (lineHeight - fontSize * 1.33) / 2);
        }

        for (var i = used; i < _gutterLabels.Count; i++)
        {
            _gutterLabels[i].Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Text colours for Markdown syntax, chosen to stay readable on Mica in both themes.</summary>
    private sealed record SyntaxPalette(Color Default, Color Heading, Color Code, Color Link, Color Quote, Color Marker, Color SearchMatch)
    {
        private static readonly SyntaxPalette Light = new(
            Color.FromArgb(0xE4, 0, 0, 0), Rgb(0x05, 0x50, 0xAE), Rgb(0x8A, 0x3D, 0x9E), Rgb(0x09, 0x69, 0xDA),
            Rgb(0x57, 0x60, 0x6A), Rgb(0xC2, 0x3B, 0x22), Rgb(0xFF, 0xE0, 0x8A));

        private static readonly SyntaxPalette Dark = new(
            Colors.White, Rgb(0x79, 0xC0, 0xFF), Rgb(0xD2, 0xA8, 0xFF), Rgb(0x58, 0xA6, 0xFF),
            Rgb(0x9D, 0xA7, 0xB3), Rgb(0xFF, 0x9B, 0x8E), Rgb(0x6B, 0x55, 0x10));

        public static SyntaxPalette For(ElementTheme theme) => theme == ElementTheme.Dark ? Dark : Light;

        public Color Get(SyntaxColor color) => color switch
        {
            SyntaxColor.Heading => Heading,
            SyntaxColor.Code => Code,
            SyntaxColor.Link => Link,
            SyntaxColor.Quote => Quote,
            SyntaxColor.Marker => Marker,
            _ => Default,
        };

        private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
    }
}
