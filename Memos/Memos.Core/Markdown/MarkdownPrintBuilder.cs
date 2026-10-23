using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Globalization;
using System.Text;

namespace Memos.Core.Markdown;

/// <summary>A stretch of text of a printed Markdown document, or an image with its alternative text.</summary>
public sealed record PrintSpan(
    string Text, bool Bold = false, bool Italic = false, bool Strikethrough = false, bool Code = false, bool Link = false, string? ImageUrl = null)
{
    /// <summary>Where a link goes; null when the link is only shown, as on paper.</summary>
    public string? LinkUrl { get; init; }

    /// <summary>Kind of token, inside highlighted code.</summary>
    public CodeToken Token { get; init; }
}

public enum CodeToken : byte
{
    None,
    Keyword,
    String,
    Comment,
    Number,
    Type,
    Preprocessor,
    Tag,
    Attribute,
}

public enum PrintBlockKind
{
    Paragraph,
    Heading,
    Code,
    Rule,
    Table,
}

public enum PrintAlignment
{
    Left,
    Center,
    Right,
}

public sealed record PrintCell(IReadOnlyList<PrintSpan> Spans, bool IsHeader, PrintAlignment Alignment);

/// <summary>
/// One block of a printed Markdown document. Lists and quotes are not containers here: each block
/// says how deep it is nested and, for the first block of a list item, which marker it carries.
/// </summary>
public sealed record PrintBlock(PrintBlockKind Kind, IReadOnlyList<PrintSpan> Spans)
{
    /// <summary>Heading level, 1 to 6.</summary>
    public int Level { get; init; }

    /// <summary>Zero-based source line the block starts at.</summary>
    public int Line { get; init; }

    /// <summary>Number of lists the block is inside of.</summary>
    public int Indent { get; init; }

    /// <summary>Bullet, number or check box that hangs to the left of the block.</summary>
    public string? Marker { get; init; }

    public int QuoteDepth { get; init; }

    /// <summary>Rows of a table; every row has the same number of cells.</summary>
    public IReadOnlyList<IReadOnlyList<PrintCell>> Rows { get; init; } = [];
}

/// <summary>
/// Reads Markdown into the blocks the preview, the printer and the PDF export lay out as native
/// text. The front matter is metadata: only the preview shows it.
/// </summary>
public static class MarkdownPrintBuilder
{
    private sealed record Options(bool HighlightCode);
    private sealed class Frame(int indent, int quoteDepth, string? marker)
    {
        public int Indent { get; } = indent;

        public int QuoteDepth { get; } = quoteDepth;

        public string? Marker { get; private set; } = marker;

        /// <summary>The marker belongs to the first block of the item only.</summary>
        public string? TakeMarker()
        {
            var marker = Marker;
            Marker = null;
            return marker;
        }
    }

    private readonly record struct Style(bool Bold = false, bool Italic = false, bool Strikethrough = false, bool Link = false, string? LinkUrl = null);

    public static IReadOnlyList<PrintBlock> Build(string text, bool highlightCode = false) =>
        Build(Markdig.Markdown.Parse(text, MarkdownService.Pipeline), highlightCode, includeFrontMatter: false);

    internal static IReadOnlyList<PrintBlock> Build(MarkdownDocument document, bool highlightCode, bool includeFrontMatter)
    {
        var blocks = new List<PrintBlock>();
        if (includeFrontMatter && document.Count > 0 && document[0] is YamlFrontMatterBlock yaml)
        {
            blocks.Add(GetFrontMatter(FrontMatter.Parse(yaml.Lines.ToString())));
        }

        AddBlocks(document, blocks, new Frame(0, 0, null), new Options(highlightCode));
        return blocks;
    }

    /// <summary>The metadata as a table of its entries, or as its raw text when it is not a simple mapping.</summary>
    private static PrintBlock GetFrontMatter(FrontMatter frontMatter) => frontMatter.Entries.Count == 0
        ? new PrintBlock(PrintBlockKind.Code, [new PrintSpan(frontMatter.Raw.Trim('\n'), Code: true)])
        : new PrintBlock(PrintBlockKind.Table, [])
        {
            Rows = frontMatter.Entries.Select(entry => (IReadOnlyList<PrintCell>)
            [
                new PrintCell([new PrintSpan(entry.Key)], IsHeader: true, PrintAlignment.Left),
                new PrintCell([new PrintSpan(entry.Value)], IsHeader: false, PrintAlignment.Left),
            ]).ToList(),
        };

    private static void AddBlocks(ContainerBlock container, List<PrintBlock> blocks, Frame frame, Options options)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case YamlFrontMatterBlock or LinkReferenceDefinitionGroup:
                    break;
                case HeadingBlock heading:
                    Add(blocks, frame, block, PrintBlockKind.Heading, GetSpans(heading.Inline), heading.Level);
                    break;
                case CodeBlock code:
                    var source = code.Lines.ToString();
                    Add(blocks, frame, block, PrintBlockKind.Code,
                        options.HighlightCode && code is FencedCodeBlock { Info: { Length: > 0 } info } && CodeHighlighter.FindLanguage(info) is { } language
                            ? CodeHighlighter.Tokenize(language, source)
                            : [new PrintSpan(source, Code: true)]);
                    break;
                case ThematicBreakBlock:
                    Add(blocks, frame, block, PrintBlockKind.Rule, []);
                    break;
                case QuoteBlock quote:
                    AddBlocks(quote, blocks, new Frame(frame.Indent, frame.QuoteDepth + 1, frame.TakeMarker()), options);
                    break;
                case ListBlock list:
                    AddList(list, blocks, frame, options);
                    break;
                case Table table:
                    blocks.Add(new PrintBlock(PrintBlockKind.Table, [])
                    {
                        Line = block.Line,
                        Indent = frame.Indent,
                        QuoteDepth = frame.QuoteDepth,
                        Marker = frame.TakeMarker(),
                        Rows = GetRows(table),
                    });
                    break;
                case FootnoteGroup footnotes:
                    Add(blocks, frame, block, PrintBlockKind.Rule, []);
                    foreach (var footnote in footnotes.OfType<Footnote>())
                    {
                        AddItem(footnote, blocks, frame, footnote.Order.ToString(CultureInfo.InvariantCulture) + ".", options);
                    }

                    break;
                case ContainerBlock other:
                    AddBlocks(other, blocks, frame, options);
                    break;
                case LeafBlock leaf:
                    Add(blocks, frame, block, PrintBlockKind.Paragraph, GetSpans(leaf.Inline));
                    break;
            }
        }
    }

    private static void AddList(ListBlock list, List<PrintBlock> blocks, Frame frame, Options options)
    {
        // A list that opens a list item keeps that item's marker on a line of its own.
        if (frame.TakeMarker() is { } pending)
        {
            blocks.Add(new PrintBlock(PrintBlockKind.Paragraph, []) { Line = list.Line, Indent = frame.Indent, QuoteDepth = frame.QuoteDepth, Marker = pending });
        }

        var number = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = list.IsOrdered ? number++.ToString(CultureInfo.InvariantCulture) + "." : "•";
            if (item.Count > 0 && item[0] is ParagraphBlock { Inline.FirstChild: TaskList task })
            {
                marker = task.Checked ? "☑" : "☐";
            }

            AddItem(item, blocks, frame, marker, options);
        }
    }

    private static void AddItem(ContainerBlock item, List<PrintBlock> blocks, Frame frame, string marker, Options options)
    {
        var inner = new Frame(frame.Indent + 1, frame.QuoteDepth, marker);
        AddBlocks(item, blocks, inner, options);
        if (inner.Marker is not null)
        {
            // An item without content still shows its bullet.
            Add(blocks, inner, item, PrintBlockKind.Paragraph, []);
        }
    }

    private static void Add(List<PrintBlock> blocks, Frame frame, Block source, PrintBlockKind kind, IReadOnlyList<PrintSpan> spans, int level = 0) =>
        blocks.Add(new PrintBlock(kind, spans) { Line = source.Line, Level = level, Indent = frame.Indent, QuoteDepth = frame.QuoteDepth, Marker = frame.TakeMarker() });

    private static List<IReadOnlyList<PrintCell>> GetRows(Table table)
    {
        var rows = new List<IReadOnlyList<PrintCell>>();
        var columns = table.OfType<TableRow>().Select(row => row.Count).DefaultIfEmpty(0).Max();
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = new List<PrintCell>(columns);
            for (var index = 0; index < columns; index++)
            {
                var spans = new List<PrintSpan>();
                if (index < row.Count && row[index] is TableCell cell)
                {
                    foreach (var leaf in cell.Descendants<LeafBlock>())
                    {
                        if (spans.Count > 0)
                        {
                            Append(spans, new PrintSpan("\n"));
                        }

                        AddSpans(leaf.Inline, spans, default);
                    }
                }

                var alignment = index < table.ColumnDefinitions.Count ? table.ColumnDefinitions[index].Alignment : null;
                cells.Add(new PrintCell(spans, row.IsHeader, alignment switch
                {
                    TableColumnAlign.Center => PrintAlignment.Center,
                    TableColumnAlign.Right => PrintAlignment.Right,
                    _ => PrintAlignment.Left,
                }));
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static List<PrintSpan> GetSpans(ContainerInline? inline)
    {
        var spans = new List<PrintSpan>();
        AddSpans(inline, spans, default);
        return spans;
    }

    private static void AddSpans(ContainerInline? container, List<PrintSpan> spans, Style style)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case TaskList:
                    break; // shown as the marker of its list item
                case LiteralInline literal:
                    // The space between a check box and its text belongs to the marker.
                    var text = literal.Content.ToString();
                    Append(spans, Styled(inline.PreviousSibling is TaskList ? text.TrimStart() : text, style));
                    break;
                case CodeInline code:
                    Append(spans, Styled(code.Content, style) with { Code = true });
                    break;
                case LineBreakInline lineBreak:
                    Append(spans, Styled(lineBreak.IsHard ? "\n" : " ", style));
                    break;
                case HtmlEntityInline entity:
                    Append(spans, Styled(entity.Transcoded.ToString(), style));
                    break;
                case AutolinkInline autolink:
                    var target = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
                    Append(spans, Styled(autolink.Url, style) with { Link = true, LinkUrl = MarkdownService.IsSafeUrl(target, isImage: false) ? target : null });
                    break;
                case FootnoteLink { IsBackLink: false } footnote:
                    Append(spans, Styled("[" + footnote.Footnote.Order.ToString(CultureInfo.InvariantCulture) + "]", style));
                    break;
                case FootnoteLink:
                    break;
                case LinkInline { IsImage: true } image:
                    var alt = GetPlainText(image);
                    Append(spans, MarkdownService.IsSafeUrl(image.Url, isImage: true) && !string.IsNullOrWhiteSpace(image.Url)
                        ? Styled(alt, style) with { ImageUrl = image.Url }
                        : Styled(alt, style));
                    break;
                case LinkInline link:
                    AddSpans(link, spans, style with
                    {
                        Link = true,
                        LinkUrl = !string.IsNullOrWhiteSpace(link.Url) && MarkdownService.IsSafeUrl(link.Url, isImage: false) ? link.Url : null,
                    });
                    break;
                case EmphasisInline emphasis:
                    AddSpans(emphasis, spans, emphasis.DelimiterChar == '~' ? style with { Strikethrough = true }
                        : emphasis.DelimiterCount >= 2 ? style with { Bold = true }
                        : style with { Italic = true });
                    break;
                case ContainerInline other:
                    AddSpans(other, spans, style);
                    break;
                case HtmlInline html:
                    Append(spans, Styled(html.Tag, style));
                    break;
            }
        }
    }

    private static PrintSpan Styled(string text, Style style) =>
        new(text, style.Bold, style.Italic, style.Strikethrough, Link: style.Link) { LinkUrl = style.LinkUrl };

    /// <summary>Adds a span, joining it to the previous one when both look the same.</summary>
    private static void Append(List<PrintSpan> spans, PrintSpan span)
    {
        if (span.Text.Length == 0 && span.ImageUrl is null)
        {
            return;
        }

        if (spans.Count > 0 && span.ImageUrl is null && spans[^1] is { ImageUrl: null } last && last with { Text = span.Text } == span)
        {
            spans[^1] = last with { Text = last.Text + span.Text };
            return;
        }

        spans.Add(span);
    }

    private static string GetPlainText(ContainerInline container)
    {
        var text = new StringBuilder();
        foreach (var node in container.Descendants())
        {
            switch (node)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
            }
        }

        return text.ToString().Trim();
    }
}
