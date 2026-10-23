using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Memos.Views;

/// <summary>
/// Rendered view of a Markdown document, laid out as native text with the same layout as printing.
/// No browser is involved, so the preview shows at once and never runs anything from the document.
/// </summary>
public sealed partial class MarkdownPreview : UserControl
{
    private readonly Dictionary<string, BitmapImage?> _images = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer _resizeTimer;
    private AppSettings? _settings;
    private MarkdownAnalysis? _analysis;
    private string? _directory;
    private string? _imagesDirectory;
    private List<(int Line, object Target)> _anchors = [];
    private double _width;
    private bool _rendering;
    private bool _dirty;
    private bool _closed;
    private (double Line, bool AtEnd)? _pendingScroll;

    public MarkdownPreview()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Refresh();

        // Dragging the splitter resizes the pane many times a second; the layout follows once it settles.
        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(120);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => Render();
        Scroller.SizeChanged += (_, _) =>
        {
            if (Math.Abs(GetContentWidth() - _width) > 1)
            {
                _resizeTimer.Start();
            }
        };
    }

    /// <summary>The user clicked a link to a local file next to the document.</summary>
    public event EventHandler<string>? LocalFileActivated;

    public void Configure(AppSettings settings) => _settings = settings;

    /// <summary>Shows the analysis. Calls made while pictures load collapse into the latest one.</summary>
    public void Show(MarkdownAnalysis? analysis, string? directory)
    {
        _analysis = analysis;
        _directory = directory;
        Render();
    }

    /// <summary>Re-renders with the current settings and theme, reading the pictures again.</summary>
    public void Refresh()
    {
        _images.Clear();
        Render();
    }

    /// <summary>Scrolls so the block that contains a source line is at the top, as in the editor.</summary>
    public void ScrollTo(double line, bool atEnd)
    {
        if (_rendering || _anchors.Count == 0)
        {
            _pendingScroll = (line, atEnd);
            return;
        }

        _pendingScroll = null;
        Scroller.UpdateLayout();
        if (atEnd)
        {
            Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: true);
            return;
        }

        var next = _anchors.FindIndex(anchor => anchor.Line > line);
        var index = (next < 0 ? _anchors.Count : next) - 1;
        if (index < 0 || line <= 0)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
            return;
        }

        // Between the start of the block and the start of the next, in proportion to the line.
        var (startLine, target) = _anchors[index];
        var top = GetTop(target);
        var bottom = next < 0 ? Body.ActualHeight : GetTop(_anchors[next].Target);
        var nextLine = next < 0 ? startLine + 1 : _anchors[next].Line;
        var fraction = nextLine > startLine ? Math.Min(1, (line - startLine) / (nextLine - startLine)) : 0;
        Scroller.ChangeView(null, Math.Max(0, top + (bottom - top) * fraction - 12), null, disableAnimation: true);
    }

    public void Close()
    {
        _closed = true;
        _resizeTimer.Stop();
        Body.Children.Clear();
        _anchors = [];
        _images.Clear();
    }

    private async void Render()
    {
        if (_closed || _settings is null)
        {
            return;
        }

        if (_rendering)
        {
            _dirty = true;
            return;
        }

        _rendering = true;
        try
        {
            do
            {
                _dirty = false;
                var blocks = _analysis?.Native ?? [];
                if (!string.Equals(_imagesDirectory, _directory, StringComparison.OrdinalIgnoreCase))
                {
                    _images.Clear();
                    _imagesDirectory = _directory;
                }

                // Only pictures not seen before are read: typing does not read them again.
                await MarkdownImages.LoadAsync(blocks, _directory, _settings.AllowRemoteImages, _images);
                if (!_dirty && !_closed)
                {
                    Build(blocks);
                }
            }
            while (_dirty && !_closed);
        }
        finally
        {
            _rendering = false;
        }

        if (_pendingScroll is { } scroll)
        {
            ScrollTo(scroll.Line, scroll.AtEnd);
        }
    }

    // shortcut: rebuilds every block on each change; reuse the unchanged blocks if large documents feel slow.
    private void Build(IReadOnlyList<PrintBlock> blocks)
    {
        var width = GetContentWidth();
        if (width <= 0)
        {
            return; // not laid out yet: the first size change renders it
        }

        _width = width;
        Body.Children.Clear();
        var (family, size) = WindowsPrintService.GetMarkdownFont(_settings!);
        var palette = ActualTheme == ElementTheme.Dark ? MarkdownPalette.Dark : MarkdownPalette.Light;
        var layout = new MarkdownLayout(new ColumnSurface(Body, width), _images, family, size, palette)
        {
            LinkClicked = OnLinkClicked,
            Selectable = true,
        };
        layout.Create(blocks);
        _anchors = layout.Anchors;
    }

    private double GetContentWidth()
    {
        var available = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
        return Math.Min(available, Body.MaxWidth) - Body.Padding.Left - Body.Padding.Right;
    }

    private double GetTop(object target) => target switch
    {
        Paragraph paragraph when paragraph.ContentStart.VisualParent is { } text =>
            text.TransformToVisual(Body).TransformPoint(default).Y + paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Y,
        UIElement element => element.TransformToVisual(Body).TransformPoint(default).Y,
        _ => 0,
    };

    private void OnLinkClicked(string url)
    {
        // Links never navigate the preview: anchors scroll it, web links open in the default
        // browser and local files open as tabs.
        if (url.StartsWith('#'))
        {
            var id = Uri.UnescapeDataString(url[1..]);
            if (_analysis?.Headings.FirstOrDefault(heading => heading.Id == id) is { } heading)
            {
                ScrollTo(heading.Line, atEnd: false);
            }
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
        else if (MarkdownImages.ResolveLocal(url, _directory) is { } path && File.Exists(path))
        {
            LocalFileActivated?.Invoke(this, path);
        }
    }
}
