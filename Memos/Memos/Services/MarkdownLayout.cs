using Memos.Core.Markdown;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.UI;

namespace Memos.Services;

/// <summary>Where <see cref="MarkdownLayout"/> places what it creates: pages, or one long column.</summary>
public abstract class MarkdownSurface
{
    public abstract double Width { get; }

    /// <summary>Pictures are scaled down to fit this height.</summary>
    public abstract double MaxImageHeight { get; }

    public abstract void AddElement(FrameworkElement element, double left, double top);

    public abstract void AddText(RichTextBlock text, double top, double lineHeight);

    public virtual void Finish()
    {
    }
}

/// <summary>The preview: everything one below the other in a panel that scrolls.</summary>
public sealed class ColumnSurface(Panel panel, double width) : MarkdownSurface
{
    public override double Width => width;

    public override double MaxImageHeight => double.PositiveInfinity;

    public override void AddElement(FrameworkElement element, double left, double top)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.Margin = new Thickness(left, panel.Children.Count == 0 ? 0 : top, 0, 0);
        panel.Children.Add(element);
    }

    public override void AddText(RichTextBlock text, double top, double lineHeight)
    {
        text.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : top, 0, 0);
        panel.Children.Add(text);
    }
}

/// <summary>
/// Fills pages from top to bottom. Text flows from one page to the next; anything that is not
/// text (a table row, a rule, a picture) is placed whole, on a new page if it does not fit.
/// Those cannot be left inside the text: the text flow does not count their height when it
/// decides where a page ends, and they would be cut off at the bottom.
/// </summary>
public sealed class Paginator(Panel host, PrintPageDescription page) : MarkdownSurface
{
    private readonly Thickness _margin = WindowsPrintService.GetMargin(page);
    private StackPanel? _stack;
    private Grid? _sheet;
    private double _used;

    public List<FrameworkElement> Pages { get; } = [];

    public override double Width => page.PageSize.Width - _margin.Left - _margin.Right;

    public double Height => page.PageSize.Height - _margin.Top - _margin.Bottom;

    public override double MaxImageHeight => Height * 0.9;

    public override void AddElement(FrameworkElement element, double left, double top)
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

    public override void AddText(RichTextBlock text, double top, double lineHeight)
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
        while (hasMore && Pages.Count < WindowsPrintService.MaxPages)
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

    public override void Finish()
    {
        if (_stack is null)
        {
            NewPage(); // an empty document prints as a blank page
        }
    }

    private void NewPage()
    {
        _stack = new StackPanel { Margin = _margin };
        _sheet = WindowsPrintService.AddPage(host, page, _stack);
        Pages.Add(_sheet);
        _used = 0;
    }
}

/// <summary>Colours of the Markdown layout; paper is always light, the preview follows the theme.</summary>
public sealed class MarkdownPalette
{
    public required Brush Text { get; init; }

    public required Brush Link { get; init; }

    public required Brush Quote { get; init; }

    public required Brush Line { get; init; }

    public required Brush Header { get; init; }

    /// <summary>One brush per <see cref="CodeToken"/>, in the order of the enum.</summary>
    public required Brush[] Tokens { get; init; }

    public static MarkdownPalette Light { get; } = Create(Colors.Black, 0xFF0B57D0, 0xFF555555, 0xFFAAAAAA, 0xFFEEEEEE,
        [0xFF000000, 0xFF0000FF, 0xFFA31515, 0xFF008000, 0xFF098658, 0xFF267F99, 0xFF808080, 0xFF800000, 0xFFE50000]);

    public static MarkdownPalette Dark { get; } = Create(Color.FromArgb(255, 230, 230, 230), 0xFF7CACF8, 0xFFAAAAAA, 0xFF555555, 0xFF2E2E2E,
        [0xFFE6E6E6, 0xFF569CD6, 0xFFCE9178, 0xFF6A9955, 0xFFB5CEA8, 0xFF4EC9B0, 0xFF9B9B9B, 0xFF569CD6, 0xFF9CDCFE]);

    private static MarkdownPalette Create(Color text, uint link, uint quote, uint line, uint header, uint[] tokens) => new()
    {
        Text = new SolidColorBrush(text),
        Link = Brush(link),
        Quote = Brush(quote),
        Line = Brush(line),
        Header = Brush(header),
        Tokens = tokens.Select(Brush).ToArray(),
    };

    private static SolidColorBrush Brush(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}

/// <summary>Turns the blocks of a Markdown document into native text and places it on a surface.</summary>
public sealed class MarkdownLayout(
    MarkdownSurface surface, IReadOnlyDictionary<string, BitmapImage?> images, string fontFamily, double fontSize, MarkdownPalette palette)
{
    public const string CodeFont = "Cascadia Mono, Consolas";

    private static readonly double[] HeadingScale = [2.0, 1.5, 1.25, 1.1, 1.0, 0.9];

    private readonly FontFamily _font = new(fontFamily);
    private readonly FontFamily _codeFont = new(CodeFont);
    private RichTextBlock? _text;
    private double _textTop;

    /// <summary>Links become clickable and call this with their target; without it they are only shown.</summary>
    public Action<string>? LinkClicked { get; init; }

    /// <summary>Whether the text can be selected and copied.</summary>
    public bool Selectable { get; init; }

    /// <summary>Where each block starts: its source line and the element or paragraph that shows it.</summary>
    public List<(int Line, object Target)> Anchors { get; } = [];

    /// <summary>Room to the left of a list item for its bullet or number.</summary>
    private double Gutter => fontSize * 1.7;

    public void Create(IReadOnlyList<PrintBlock> blocks)
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
                AddElement(block, new Border { Width = Math.Max(1, surface.Width - left), Height = 1, Background = palette.Line }, left, top);
            }
            else if (GetPictures(block) is { Count: > 0 } pictures)
            {
                FlushText();
                foreach (var picture in pictures)
                {
                    AddElement(block, CreateImage(picture, surface.Width - left), left, top);
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
                        Foreground = palette.Text,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = Selectable,
                    };
                    _textTop = top;
                    top = 0;
                }

                var paragraph = CreateParagraph(block, left, top);
                _text.Blocks.Add(paragraph);
                Anchors.Add((block.Line, paragraph));
            }

            previous = block;
        }

        FlushText();
        surface.Finish();
    }

    private void AddElement(PrintBlock block, FrameworkElement element, double left, double top)
    {
        surface.AddElement(element, left, top);
        Anchors.Add((block.Line, element));
    }

    private void FlushText()
    {
        if (_text is not null)
        {
            surface.AddText(_text, _textTop, fontSize * 1.35);
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
            if (span.ImageUrl is { } url && images.TryGetValue(url, out var image) && image is { PixelWidth: > 0 })
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

    /// <summary>One image pixel is one XAML unit, but never larger than the room there is.</summary>
    private Image CreateImage(BitmapImage image, double availableWidth)
    {
        var scale = Math.Min(1, Math.Min(Math.Max(1, availableWidth) / image.PixelWidth, surface.MaxImageHeight / image.PixelHeight));
        return new Image { Source = image, Width = image.PixelWidth * scale, Height = image.PixelHeight * scale, Stretch = Stretch.Fill };
    }

    private Paragraph CreateParagraph(PrintBlock block, double left, double top)
    {
        var paragraph = new Paragraph { Margin = new Thickness(left, top, 0, 0) };
        if (block.QuoteDepth > 0)
        {
            paragraph.Foreground = palette.Quote;
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
                AddInlines(paragraph.Inlines, block.Spans, surface.Width - left);
                break;
            case PrintBlockKind.Code:
                paragraph.FontFamily = _codeFont;
                paragraph.FontSize = fontSize * 0.88;
                paragraph.Margin = new Thickness(left + fontSize * 0.8, top, 0, 0);
                var column = 0;
                for (var index = 0; index < block.Spans.Count; index++)
                {
                    var span = block.Spans[index];
                    var text = index == block.Spans.Count - 1 ? span.Text.TrimEnd('\n') : span.Text;
                    var run = new Run { Text = WindowsPrintService.ExpandTabs(text, 4, ref column) };
                    if (span.Token != CodeToken.None)
                    {
                        run.Foreground = palette.Tokens[(int)span.Token];
                    }

                    paragraph.Inlines.Add(run);
                }

                break;
            default:
                AddInlines(paragraph.Inlines, block.Spans, surface.Width - left);
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
            if (span.ImageUrl is { } url && images.TryGetValue(url, out var image) && image is { PixelWidth: > 0 })
            {
                inlines.Add(new InlineUIContainer { Child = CreateImage(image, availableWidth) });
                continue;
            }

            var run = new Run { Text = span.Text };
            if (span.Bold) run.FontWeight = FontWeights.Bold;
            if (span.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (span.Strikethrough) run.TextDecorations |= Windows.UI.Text.TextDecorations.Strikethrough;
            if (span.Code) run.FontFamily = _codeFont;
            if (!span.Link)
            {
                inlines.Add(run);
            }
            else if (span.LinkUrl is { } target && LinkClicked is { } clicked)
            {
                var link = new Hyperlink { Foreground = palette.Link };
                ToolTipService.SetToolTip(link, target);
                link.Click += (_, _) => clicked(target);
                link.Inlines.Add(run);
                inlines.Add(link);
            }
            else
            {
                run.TextDecorations |= Windows.UI.Text.TextDecorations.Underline;
                run.Foreground = palette.Link;
                inlines.Add(run);
            }
        }
    }

    private void AddTable(PrintBlock block, double left, double top)
    {
        if (block.Rows.Count == 0)
        {
            return;
        }

        var padding = new Thickness(fontSize * 0.5, fontSize * 0.2, fontSize * 0.5, fontSize * 0.25);
        var widths = GetColumnWidths(block.Rows, padding.Left + padding.Right, surface.Width - left);
        for (var index = 0; index < block.Rows.Count; index++)
        {
            var row = new Grid();
            for (var column = 0; column < widths.Length; column++)
            {
                var cell = block.Rows[index][column];
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[column]) });
                var border = new Border
                {
                    BorderBrush = palette.Line,
                    // Every cell is boxed, so a table that continues on another page is closed on both.
                    BorderThickness = new Thickness(column == 0 ? 1 : 0, 1, 1, 1),
                    Margin = new Thickness(0, index == 0 ? 0 : -1, 0, 0),
                    Background = cell.IsHeader ? palette.Header : null,
                    Padding = padding,
                    Child = CreateCellText(cell, widths[column] - padding.Left - padding.Right, wrap: true),
                };
                Grid.SetColumn(border, column);
                row.Children.Add(border);
            }

            if (index == 0)
            {
                AddElement(block, row, left, top);
            }
            else
            {
                surface.AddElement(row, left, 0);
            }
        }
    }

    private TextBlock CreateCellText(PrintCell cell, double availableWidth, bool wrap)
    {
        var text = new TextBlock
        {
            FontFamily = _font,
            FontSize = fontSize,
            Foreground = palette.Text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsTextSelectionEnabled = Selectable && wrap,
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

/// <summary>Decodes the pictures of a document, from the disk, the document itself or, if allowed, the web.</summary>
public static class MarkdownImages
{
    /// <summary>Every picture of the blocks; null for one that cannot be shown, so its alternative text is.</summary>
    public static async Task LoadAsync(IReadOnlyList<PrintBlock> blocks, string? baseDirectory, bool allowRemote, Dictionary<string, BitmapImage?> images)
    {
        var urls = blocks.SelectMany(block => block.Spans.Concat(block.Rows.SelectMany(row => row).SelectMany(cell => cell.Spans)))
            .Select(span => span.ImageUrl).OfType<string>().Distinct(StringComparer.Ordinal).Where(url => !images.ContainsKey(url)).ToList();
        foreach (var url in urls)
        {
            images[url] = null;
            try
            {
                if (await ReadAsync(url, baseDirectory, allowRemote) is { Length: > 0 } bytes)
                {
                    var image = new BitmapImage();
                    using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                    await image.SetSourceAsync(stream);
                    images[url] = image;
                }
            }
            catch (Exception)
            {
                // Missing, unreadable or not a picture Windows can decode: its alternative text is shown.
            }
        }
    }

    private static async Task<byte[]?> ReadAsync(string url, string? baseDirectory, bool allowRemote)
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
            if (!allowRemote)
            {
                return null;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return await client.GetByteArrayAsync(url);
        }

        return ResolveLocal(url, baseDirectory) is { } path && File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    /// <summary>The file a link or picture names on disk, relative to the document's folder; or null.</summary>
    public static string? ResolveLocal(string url, string? baseDirectory)
    {
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.LocalPath : null;
        }

        var end = url.IndexOfAny(['?', '#']);
        var path = Uri.UnescapeDataString(end < 0 ? url : url[..end]).Replace('/', Path.DirectorySeparatorChar);
        if (path.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathRooted(path))
        {
            if (baseDirectory is null)
            {
                return null;
            }

            path = Path.Combine(baseDirectory, path);
        }

        return Path.GetFullPath(path);
    }
}
