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

    private const int MaxPages = 5000;

    private const string CodeFont = "Cascadia Mono, Consolas";

    private static readonly double[] HeadingScale = [2.0, 1.5, 1.25, 1.1, 1.0, 0.9];

    private Session? _session;

    public Task PrintTextAsync(string title, IReadOnlyList<RichTextRun> runs, bool monospaced)
    {
        var family = monospaced ? settings.Current.FontFamily + ", " + CodeFont : "Segoe UI";
        var tabSize = monospaced ? settings.Current.TabSize : 0;
        return ShowAsync(title, (host, page) => Flow(host, page, CreateText(runs, family, tabSize)));
    }

    public async Task PrintMarkdownAsync(string title, IReadOnlyList<PrintBlock> blocks, string? baseDirectory)
    {
        // Pictures are decoded first: the text can only be split into pages once their size is known.
        var images = await LoadImagesAsync(blocks, baseDirectory);
        var current = settings.Current;
        var family = current.PreviewStyle == PreviewStyle.Serif ? "Georgia, Cambria, Times New Roman" : "Segoe UI";
        var size = current.PreviewStyle == PreviewStyle.Compact ? current.PreviewFontSize * 0.9 : current.PreviewFontSize;
        await ShowAsync(title, (host, page) => new MarkdownLayout(new Paginator(host, page), images, family, size).Create(blocks));
    }

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

    private static Grid AddPage(Panel host, PrintPageDescription page, FrameworkElement content)
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

    private static Thickness GetMargin(PrintPageDescription page)
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
    private static string ExpandTabs(string text, int tabSize, ref int column)
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

    // ---- Markdown -------------------------------------------------------------------------------------

    /// <summary>
    /// Fills pages from top to bottom. Text flows from one page to the next; anything that is not
    /// text (a table row, a rule, a picture) is placed whole, on a new page if it does not fit.
    /// Those cannot be left inside the text: the text flow does not count their height when it
    /// decides where a page ends, and they would be cut off at the bottom.
    /// </summary>
    private sealed class Paginator(Panel host, PrintPageDescription page)
    {
        private readonly Thickness _margin = GetMargin(page);
        private StackPanel? _stack;
        private Grid? _sheet;
        private double _used;

        public List<FrameworkElement> Pages { get; } = [];

        public double Width => page.PageSize.Width - _margin.Left - _margin.Right;

        public double Height => page.PageSize.Height - _margin.Top - _margin.Bottom;

        public void AddElement(FrameworkElement element, double left, double top)
        {
            element.HorizontalAlignment = HorizontalAlignment.Left;
            element.Measure(new Size(Math.Max(1, Width - left), double.PositiveInfinity));
            var height = element.DesiredSize.Height;
            if (_stack is null || (_used > 0 && _used + top + height > Height))
            {
                NewPage();
            }

            // Space between blocks separates them; at the top of a page there is nothing to separate.
            top = _used > 0 ? top : 0;
            element.Margin = new Thickness(left, top, 0, 0);
            _stack!.Children.Add(element);
            _used += top + height;
        }

        public void AddText(RichTextBlock text, double top, double lineHeight)
        {
            if (_stack is null || (_used > 0 && Height - _used - top < lineHeight * 1.5))
            {
                NewPage();
            }

            top = _used > 0 ? top : 0;
            text.Margin = new Thickness(0, top, 0, 0);
            text.MaxHeight = Math.Max(1, Height - _used - top);
            _stack!.Children.Add(text);
            _sheet!.UpdateLayout();
            _used += top + text.ActualHeight;

            var hasMore = text.HasOverflowContent;
            RichTextBlockOverflow? last = null;
            while (hasMore && Pages.Count < MaxPages)
            {
                NewPage();
                var overflow = new RichTextBlockOverflow { MaxHeight = Height };
                if (last is null)
                {
                    text.OverflowContentTarget = overflow;
                }
                else
                {
                    last.OverflowContentTarget = overflow;
                }

                _stack.Children.Add(overflow);
                _sheet.UpdateLayout();
                _used = overflow.ActualHeight;
                hasMore = overflow.HasOverflowContent;
                last = overflow;
            }
        }

        public void Finish()
        {
            if (_stack is null)
            {
                NewPage(); // an empty document prints as a blank page
            }
        }

        private void NewPage()
        {
            _stack = new StackPanel { Margin = _margin };
            _sheet = AddPage(host, page, _stack);
            Pages.Add(_sheet);
            _used = 0;
        }
    }

    /// <summary>Turns the blocks of a Markdown document into native text and places it on pages.</summary>
    private sealed class MarkdownLayout(Paginator pages, IReadOnlyDictionary<string, BitmapImage> images, string fontFamily, double fontSize)
    {
        private static readonly SolidColorBrush TextBrush = new(Colors.Black);
        private static readonly SolidColorBrush LinkBrush = new(Color.FromArgb(255, 11, 87, 208));
        private static readonly SolidColorBrush QuoteBrush = new(Color.FromArgb(255, 85, 85, 85));
        private static readonly SolidColorBrush LineBrush = new(Color.FromArgb(255, 170, 170, 170));
        private static readonly SolidColorBrush HeaderBrush = new(Color.FromArgb(255, 238, 238, 238));

        private readonly FontFamily _font = new(fontFamily);
        private readonly FontFamily _codeFont = new(CodeFont);
        private RichTextBlock? _text;
        private double _textTop;

        /// <summary>Room to the left of a list item for its bullet or number.</summary>
        private double Gutter => fontSize * 1.7;

        public List<FrameworkElement> Create(IReadOnlyList<PrintBlock> blocks)
        {
            PrintBlock? previous = null;
            foreach (var block in blocks)
            {
                var left = block.QuoteDepth * fontSize * 1.2 + block.Indent * Gutter;
                var top = previous is null ? 0
                    : block.Kind == PrintBlockKind.Heading ? fontSize * 1.1
                    : block.Indent > 0 && previous.Indent > 0 ? fontSize * 0.25
                    : fontSize * 0.7;

                if (block.Kind == PrintBlockKind.Table)
                {
                    FlushText();
                    AddTable(block, left, top);
                }
                else if (block.Kind == PrintBlockKind.Rule)
                {
                    FlushText();
                    pages.AddElement(new Border { Width = Math.Max(1, pages.Width - left), Height = 1, Background = LineBrush }, left, top);
                }
                else if (GetPictures(block) is { Count: > 0 } pictures)
                {
                    FlushText();
                    foreach (var picture in pictures)
                    {
                        pages.AddElement(CreateImage(picture, pages.Width - left), left, top);
                    }
                }
                else
                {
                    if (_text is null)
                    {
                        _text = new RichTextBlock
                        {
                            FontFamily = _font,
                            FontSize = fontSize,
                            Foreground = TextBrush,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = false,
                        };
                        _textTop = top;
                        top = 0;
                    }

                    _text.Blocks.Add(CreateParagraph(block, left, top));
                }

                previous = block;
            }

            FlushText();
            pages.Finish();
            return pages.Pages;
        }

        private void FlushText()
        {
            if (_text is not null)
            {
                pages.AddText(_text, _textTop, fontSize * 1.35);
                _text = null;
            }
        }

        /// <summary>The pictures of a paragraph that holds nothing else, which is how a figure is written.</summary>
        private List<BitmapImage>? GetPictures(PrintBlock block)
        {
            if (block.Kind != PrintBlockKind.Paragraph || block.Marker is not null)
            {
                return null;
            }

            var pictures = new List<BitmapImage>();
            foreach (var span in block.Spans)
            {
                if (span.ImageUrl is { } url && images.TryGetValue(url, out var image) && image.PixelWidth > 0)
                {
                    pictures.Add(image);
                }
                else if (span.ImageUrl is not null || !string.IsNullOrWhiteSpace(span.Text))
                {
                    return null;
                }
            }

            return pictures;
        }

        /// <summary>One image pixel is one XAML unit, as in the preview, but never larger than the page.</summary>
        private Image CreateImage(BitmapImage image, double availableWidth)
        {
            var scale = Math.Min(1, Math.Min(availableWidth / image.PixelWidth, pages.Height * 0.9 / image.PixelHeight));
            return new Image { Source = image, Width = image.PixelWidth * scale, Height = image.PixelHeight * scale, Stretch = Stretch.Fill };
        }

        private Paragraph CreateParagraph(PrintBlock block, double left, double top)
        {
            var paragraph = new Paragraph { Margin = new Thickness(left, top, 0, 0) };
            if (block.QuoteDepth > 0)
            {
                paragraph.Foreground = QuoteBrush;
            }

            if (block.Marker is { } marker)
            {
                AddMarker(paragraph, marker);
            }

            switch (block.Kind)
            {
                case PrintBlockKind.Heading:
                    paragraph.FontSize = fontSize * HeadingScale[Math.Clamp(block.Level, 1, 6) - 1];
                    paragraph.FontWeight = FontWeights.SemiBold;
                    AddInlines(paragraph.Inlines, block.Spans, pages.Width - left);
                    break;
                case PrintBlockKind.Code:
                    paragraph.FontFamily = _codeFont;
                    paragraph.FontSize = fontSize * 0.88;
                    paragraph.Margin = new Thickness(left + fontSize * 0.8, top, 0, 0);
                    var column = 0;
                    paragraph.Inlines.Add(new Run { Text = ExpandTabs(string.Concat(block.Spans.Select(s => s.Text)).TrimEnd('\n'), 4, ref column) });
                    break;
                default:
                    AddInlines(paragraph.Inlines, block.Spans, pages.Width - left);
                    break;
            }

            return paragraph;
        }

        /// <summary>
        /// The marker hangs to the left of the text: the first line starts one gutter earlier, and
        /// an empty box after the marker brings the text back to where the other lines start.
        /// </summary>
        private void AddMarker(Paragraph paragraph, string marker)
        {
            var width = Measure(new TextBlock { Text = marker, FontFamily = _font, FontSize = fontSize }).Width;
            var gap = Math.Max(fontSize * 0.3, Gutter - width);
            paragraph.TextIndent = -(width + gap);
            paragraph.Inlines.Add(new Run { Text = marker, FontSize = fontSize, FontWeight = FontWeights.Normal, FontFamily = _font });
            paragraph.Inlines.Add(new InlineUIContainer { Child = new Border { Width = gap, Height = 1 } });
        }

        private void AddInlines(InlineCollection inlines, IReadOnlyList<PrintSpan> spans, double availableWidth)
        {
            foreach (var span in spans)
            {
                if (span.ImageUrl is { } url && images.TryGetValue(url, out var image) && image.PixelWidth > 0)
                {
                    inlines.Add(new InlineUIContainer { Child = CreateImage(image, availableWidth) });
                    continue;
                }

                var run = new Run { Text = span.Text };
                if (span.Bold) run.FontWeight = FontWeights.Bold;
                if (span.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (span.Strikethrough) run.TextDecorations |= Windows.UI.Text.TextDecorations.Strikethrough;
                if (span.Code) run.FontFamily = _codeFont;
                if (span.Link)
                {
                    run.TextDecorations |= Windows.UI.Text.TextDecorations.Underline;
                    run.Foreground = LinkBrush;
                }

                inlines.Add(run);
            }
        }

        private void AddTable(PrintBlock block, double left, double top)
        {
            if (block.Rows.Count == 0)
            {
                return;
            }

            var padding = new Thickness(fontSize * 0.5, fontSize * 0.2, fontSize * 0.5, fontSize * 0.25);
            var widths = GetColumnWidths(block.Rows, padding.Left + padding.Right, pages.Width - left);
            for (var index = 0; index < block.Rows.Count; index++)
            {
                var row = new Grid();
                for (var column = 0; column < widths.Length; column++)
                {
                    var cell = block.Rows[index][column];
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[column]) });
                    var border = new Border
                    {
                        BorderBrush = LineBrush,
                        // Every cell is boxed, so a table that continues on another page is closed on both.
                        BorderThickness = new Thickness(column == 0 ? 1 : 0, 1, 1, 1),
                        Margin = new Thickness(0, index == 0 ? 0 : -1, 0, 0),
                        Background = cell.IsHeader ? HeaderBrush : null,
                        Padding = padding,
                        Child = CreateCellText(cell, widths[column] - padding.Left - padding.Right, wrap: true),
                    };
                    Grid.SetColumn(border, column);
                    row.Children.Add(border);
                }

                pages.AddElement(row, left, index == 0 ? top : 0);
            }
        }

        private TextBlock CreateCellText(PrintCell cell, double availableWidth, bool wrap)
        {
            var text = new TextBlock
            {
                FontFamily = _font,
                FontSize = fontSize,
                Foreground = TextBrush,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                FontWeight = cell.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = cell.Alignment switch
                {
                    PrintAlignment.Center => TextAlignment.Center,
                    PrintAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                },
            };
            AddInlines(text.Inlines, cell.Spans, availableWidth);
            return text;
        }

        /// <summary>Each column as wide as its content, all narrowed in proportion when they do not fit.</summary>
        private double[] GetColumnWidths(IReadOnlyList<IReadOnlyList<PrintCell>> rows, double padding, double available)
        {
            var widths = new double[rows[0].Count];
            foreach (var row in rows)
            {
                for (var column = 0; column < widths.Length; column++)
                {
                    var natural = Measure(CreateCellText(row[column], available, wrap: false)).Width;
                    widths[column] = Math.Max(widths[column], Math.Ceiling(natural) + padding + 2);
                }
            }

            var total = widths.Sum();
            if (total > available && total > 0)
            {
                for (var column = 0; column < widths.Length; column++)
                {
                    widths[column] = Math.Floor(widths[column] * available / total);
                }
            }

            return widths;
        }

        private static Size Measure(TextBlock text)
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return text.DesiredSize;
        }
    }

    // ---- Pictures -------------------------------------------------------------------------------------

    private async Task<IReadOnlyDictionary<string, BitmapImage>> LoadImagesAsync(IReadOnlyList<PrintBlock> blocks, string? baseDirectory)
    {
        var images = new Dictionary<string, BitmapImage>(StringComparer.Ordinal);
        var urls = blocks.SelectMany(block => block.Spans.Concat(block.Rows.SelectMany(row => row).SelectMany(cell => cell.Spans)))
            .Select(span => span.ImageUrl).OfType<string>().Distinct(StringComparer.Ordinal);
        foreach (var url in urls)
        {
            try
            {
                if (await ReadImageAsync(url, baseDirectory) is { Length: > 0 } bytes)
                {
                    var image = new BitmapImage();
                    using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                    await image.SetSourceAsync(stream);
                    images[url] = image;
                }
            }
            catch (Exception)
            {
                // Missing, unreadable or not a picture Windows can decode: its alternative text is printed.
            }
        }

        return images;
    }

    /// <summary>The bytes of a picture, from the same places the preview takes them: the disk, the document itself or, if allowed, the web.</summary>
    private async Task<byte[]?> ReadImageAsync(string url, string? baseDirectory)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            return comma > 0 && url.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(url[(comma + 1)..])
                : null;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!settings.Current.AllowRemoteImages)
            {
                return null;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return await client.GetByteArrayAsync(url);
        }

        string path;
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            path = new Uri(url).LocalPath;
        }
        else
        {
            var end = url.IndexOfAny(['?', '#']);
            path = Uri.UnescapeDataString(end < 0 ? url : url[..end]).Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathRooted(path))
            {
                if (baseDirectory is null)
                {
                    return null;
                }

                path = Path.Combine(baseDirectory, path);
            }
        }

        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }
}
