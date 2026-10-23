using Memos.Core.Helpers;
using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.UI;

namespace Memos.Services;

/// <summary>
/// Prints through the print dialog of Windows, the one with the preview, printer and paper options.
/// Every kind of document is laid out here as native text; nothing goes through the browser engine.
/// </summary>
public sealed class WindowsPrintService(WindowContext context, ISettingsService settings) : IPrintService
{
    /// <summary>11 pt, in the 1/96 inch units of XAML.</summary>
    private const double TextFontSize = 11 * 96.0 / 72.0;

    /// <summary>Page margin (3/4 inch), unless the printer needs more.</summary>
    private const double PageMargin = 72;

    internal const int MaxPages = 5000;

    private Session? _session;

    public Task PrintTextAsync(string title, IReadOnlyList<RichTextRun> runs, bool monospaced)
    {
        var family = monospaced ? settings.Current.FontFamily + ", " + MarkdownLayout.CodeFont : "Segoe UI";
        var tabSize = monospaced ? settings.Current.TabSize : 0;
        return ShowAsync(title, (host, page) => Flow(host, page, CreateText(runs, family, tabSize)));
    }

    public async Task PrintMarkdownAsync(string title, IReadOnlyList<PrintBlock> blocks, string? baseDirectory)
    {
        // Pictures are decoded first: the text can only be split into pages once their size is known.
        var images = new Dictionary<string, BitmapImage?>(StringComparer.Ordinal);
        await MarkdownImages.LoadAsync(blocks, baseDirectory, settings.Current.AllowRemoteImages, images);
        var (family, size) = GetMarkdownFont(settings.Current);
        await ShowAsync(title, (host, page) =>
        {
            var pages = new Paginator(host, page);
            new MarkdownLayout(pages, images, family, size, MarkdownPalette.Light).Create(blocks);
            return pages.Pages;
        });
    }

    /// <summary>The font of rendered Markdown, as the preview style sets it.</summary>
    public static (string Family, double Size) GetMarkdownFont(AppSettings current) => (
        current.PreviewStyle == PreviewStyle.Serif ? "Georgia, Cambria, Times New Roman" : "Segoe UI",
        current.PreviewStyle == PreviewStyle.Compact ? current.PreviewFontSize * 0.9 : current.PreviewFontSize);

    // ---- Print dialog ---------------------------------------------------------------------------------

    private async Task ShowAsync(string title, Func<Panel, PrintPageDescription, List<FrameworkElement>> paginate)
    {
        var host = context.HiddenHost ?? throw new InvalidOperationException("The window is not ready.");
        if (!PrintManager.IsSupported())
        {
            throw new InvalidOperationException("Printing is not available on this device.");
        }

        // The dialog reports its end through the print task; a job left behind is released here.
        _session?.Dispose();
        var window = Win32Interop.GetWindowFromWindowId(context.WindowId);
        var session = _session = new Session(title, host, PrintManagerInterop.GetForWindow(window), host.DispatcherQueue, paginate);
        try
        {
            await PrintManagerInterop.ShowPrintUIForWindowAsync(window);
        }
        catch (Exception ex)
        {
            session.Dispose();
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>One print job: the pages shown in the preview are the ones sent to the printer.</summary>
    private sealed class Session : IDisposable
    {
        private readonly string _title;
        private readonly Panel _host;
        private readonly PrintManager _manager;
        private readonly DispatcherQueue _dispatcher;
        private readonly Func<Panel, PrintPageDescription, List<FrameworkElement>> _paginate;
        private readonly PrintDocument _document = new();
        private readonly IPrintDocumentSource _source;
        private List<FrameworkElement> _pages = [];
        private bool _disposed;

        public Session(string title, Panel host, PrintManager manager, DispatcherQueue dispatcher,
            Func<Panel, PrintPageDescription, List<FrameworkElement>> paginate)
        {
            _title = title;
            _host = host;
            _manager = manager;
            _dispatcher = dispatcher;
            _paginate = paginate;
            _source = _document.DocumentSource;
            _document.Paginate += OnPaginate;
            _document.GetPreviewPage += OnGetPreviewPage;
            _document.AddPages += OnAddPages;
            _manager.PrintTaskRequested += OnPrintTaskRequested;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _manager.PrintTaskRequested -= OnPrintTaskRequested;
            _document.Paginate -= OnPaginate;
            _document.GetPreviewPage -= OnGetPreviewPage;
            _document.AddPages -= OnAddPages;
            RemovePages();
        }

        private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs e)
        {
            var task = e.Request.CreatePrintTask(_title, request => request.SetSource(_source));

            // Printed, cancelled or failed: either way the pages are no longer needed.
            task.Completed += (_, _) => _dispatcher.TryEnqueue(Dispose);
        }

        /// <summary>Raised when the dialog opens and again whenever paper, orientation or margins change.</summary>
        private void OnPaginate(object sender, PaginateEventArgs e)
        {
            Paginate(e.PrintTaskOptions);
            _document.SetPreviewPageCount(_pages.Count, PreviewPageCountType.Final);
        }

        private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
        {
            if (e.PageNumber >= 1 && e.PageNumber <= _pages.Count)
            {
                _document.SetPreviewPage(e.PageNumber, _pages[e.PageNumber - 1]);
            }
        }

        private void OnAddPages(object sender, AddPagesEventArgs e)
        {
            if (_pages.Count == 0)
            {
                Paginate(e.PrintTaskOptions);
            }

            foreach (var page in _pages)
            {
                _document.AddPage(page);
            }

            _document.AddPagesComplete();
        }

        private void Paginate(PrintTaskOptions options)
        {
            if (_disposed)
            {
                return;
            }

            RemovePages();
            _pages = _paginate(_host, options.GetPageDescription(0));
        }

        private void RemovePages()
        {
            foreach (var page in _pages)
            {
                _host.Children.Remove(page);
            }

            _pages = [];
        }
    }

    // ---- Pages ----------------------------------------------------------------------------------------

    /// <summary>
    /// Flows the text through as many pages as it needs. The pages live in the window's hidden
    /// panel because text is only measured, and so split into pages, inside a visual tree.
    /// </summary>
    private static List<FrameworkElement> Flow(Panel host, PrintPageDescription page, RichTextBlock text)
    {
        var margin = GetMargin(page);
        text.Margin = margin;
        text.Foreground = new SolidColorBrush(Colors.Black);
        text.TextWrapping = TextWrapping.Wrap;
        text.IsTextSelectionEnabled = false;

        var pages = new List<FrameworkElement> { AddPage(host, page, text) };
        var hasMore = text.HasOverflowContent;
        RichTextBlockOverflow? last = null;
        while (hasMore && pages.Count < MaxPages)
        {
            var overflow = new RichTextBlockOverflow { Margin = margin };
            if (last is null)
            {
                text.OverflowContentTarget = overflow;
            }
            else
            {
                last.OverflowContentTarget = overflow;
            }

            pages.Add(AddPage(host, page, overflow));
            hasMore = overflow.HasOverflowContent;
            last = overflow;
        }

        return pages;
    }

    internal static Grid AddPage(Panel host, PrintPageDescription page, FrameworkElement content)
    {
        var sheet = new Grid
        {
            Width = page.PageSize.Width,
            Height = page.PageSize.Height,
            Background = new SolidColorBrush(Colors.White),
            RequestedTheme = ElementTheme.Light,
        };
        sheet.Children.Add(content);
        host.Children.Add(sheet);
        sheet.InvalidateMeasure();
        sheet.UpdateLayout();
        return sheet;
    }

    internal static Thickness GetMargin(PrintPageDescription page)
    {
        var printable = page.ImageableRect;
        return new Thickness(
            Math.Max(PageMargin, printable.Left),
            Math.Max(PageMargin, printable.Top),
            Math.Max(PageMargin, page.PageSize.Width - printable.Right),
            Math.Max(PageMargin, page.PageSize.Height - printable.Bottom));
    }

    // ---- Text and rich text ---------------------------------------------------------------------------

    private static RichTextBlock CreateText(IReadOnlyList<RichTextRun> runs, string fontFamily, int tabSize)
    {
        var paragraph = new Paragraph();
        var column = 0;
        foreach (var run in runs)
        {
            var inline = new Run { Text = tabSize > 0 ? ExpandTabs(run.Text, tabSize, ref column) : run.Text };
            if (run.Bold) inline.FontWeight = FontWeights.Bold;
            if (run.Italic) inline.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (run.Underline) inline.TextDecorations |= Windows.UI.Text.TextDecorations.Underline;
            if (run.Strikethrough) inline.TextDecorations |= Windows.UI.Text.TextDecorations.Strikethrough;
            paragraph.Inlines.Add(inline);
        }

        var text = new RichTextBlock { FontFamily = new FontFamily(fontFamily), FontSize = TextFontSize };
        text.Blocks.Add(paragraph);
        return text;
    }

    /// <summary>Tab stops of the editor, written as spaces: printed text has no tab stops of its own.</summary>
    internal static string ExpandTabs(string text, int tabSize, ref int column)
    {
        if (!text.Contains('\t'))
        {
            var lastBreak = text.LastIndexOf('\n');
            column = lastBreak < 0 ? column + text.Length : text.Length - lastBreak - 1;
            return text;
        }

        var expanded = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c == '\t')
            {
                var spaces = tabSize - column % tabSize;
                expanded.Append(' ', spaces);
                column += spaces;
            }
            else
            {
                expanded.Append(c);
                column = c == '\n' ? 0 : column + 1;
            }
        }

        return expanded.ToString();
    }
}
