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
    string Text, bool Bold = false, bool Italic = false, bool Strikethrough = false, bool Code = false, bool Link = false, string? ImageUrl = null);

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

    /// <summary>Number of lists the block is inside of.</summary>
    public int Indent { get; init; }

    /// <summary>Bullet, number or check box that hangs to the left of the block.</summary>
    public string? Marker { get; init; }

    public int QuoteDepth { get; init; }

    /// <summary>Rows of a table; every row has the same number of cells.</summary>
    public IReadOnlyList<IReadOnlyList<PrintCell>> Rows { get; init; } = [];
}

/// <summary>
/// Reads Markdown into the blocks the printer lays out as native text, with the same syntax
/// extensions as the preview. The front matter is metadata and is left out, as in the exports.
/// </summary>
public static class MarkdownPrintBuilder
{
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

    private readonly record struct Style(bool Bold = false, bool Italic = false, bool Strikethrough = false, bool Link = false);

    public static IReadOnlyList<PrintBlock> Build(string text)
    {
        var blocks = new List<PrintBlock>();
        AddBlocks(Markdig.Markdown.Parse(text, MarkdownService.Pipeline), blocks, new Frame(0, 0, null));
        return blocks;
    }

    private static void AddBlocks(ContainerBlock container, List<PrintBlock> blocks, Frame frame)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case YamlFrontMatterBlock or LinkReferenceDefinitionGroup:
                    break;
                case HeadingBlock heading:
                    Add(blocks, frame, PrintBlockKind.Heading, GetSpans(heading.Inline), heading.Level);
                    break;
                case CodeBlock code:
                    Add(blocks, frame, PrintBlockKind.Code, [new PrintSpan(code.Lines.ToString(), Code: true)]);
                    break;
                case ThematicBreakBlock:
                    Add(blocks, frame, PrintBlockKind.Rule, []);
                    break;
                case QuoteBlock quote:
                    AddBlocks(quote, blocks, new Frame(frame.Indent, frame.QuoteDepth + 1, frame.TakeMarker()));
                    break;
                case ListBlock list:
                    AddList(list, blocks, frame);
                    break;
                case Table table:
                    blocks.Add(new PrintBlock(PrintBlockKind.Table, [])
                    {
                        Indent = frame.Indent,
                        QuoteDepth = frame.QuoteDepth,
                        Marker = frame.TakeMarker(),
                        Rows = GetRows(table),
                    });
                    break;
                case FootnoteGroup footnotes:
                    Add(blocks, frame, PrintBlockKind.Rule, []);
                    foreach (var footnote in footnotes.OfType<Footnote>())
                    {
                        AddItem(footnote, blocks, frame, footnote.Order.ToString(CultureInfo.InvariantCulture) + ".");
                    }

                    break;
                case ContainerBlock other:
                    AddBlocks(other, blocks, frame);
                    break;
                case LeafBlock leaf:
                    Add(blocks, frame, PrintBlockKind.Paragraph, GetSpans(leaf.Inline));
                    break;
            }
        }
    }

    private static void AddList(ListBlock list, List<PrintBlock> blocks, Frame frame)
    {
        // A list that opens a list item keeps that item's marker on a line of its own.
        if (frame.TakeMarker() is { } pending)
        {
            blocks.Add(new PrintBlock(PrintBlockKind.Paragraph, []) { Indent = frame.Indent, QuoteDepth = frame.QuoteDepth, Marker = pending });
        }

        var number = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = list.IsOrdered ? number++.ToString(CultureInfo.InvariantCulture) + "." : "•";
            if (item.Count > 0 && item[0] is ParagraphBlock { Inline.FirstChild: TaskList task })
            {
                marker = task.Checked ? "☑" : "☐";
            }

            AddItem(item, blocks, frame, marker);
        }
    }

    private static void AddItem(ContainerBlock item, List<PrintBlock> blocks, Frame frame, string marker)
    {
        var inner = new Frame(frame.Indent + 1, frame.QuoteDepth, marker);
        AddBlocks(item, blocks, inner);
        if (inner.Marker is not null)
        {
            // An item without content still shows its bullet.
            Add(blocks, inner, PrintBlockKind.Paragraph, []);
        }
    }

    private static void Add(List<PrintBlock> blocks, Frame frame, PrintBlockKind kind, IReadOnlyList<PrintSpan> spans, int level = 0) =>
        blocks.Add(new PrintBlock(kind, spans) { Level = level, Indent = frame.Indent, QuoteDepth = frame.QuoteDepth, Marker = frame.TakeMarker() });

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
                    Append(spans, Styled(autolink.Url, style) with { Link = true });
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
                    AddSpans(link, spans, style with { Link = true });
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

    private static PrintSpan Styled(string text, Style style) => new(text, style.Bold, style.Italic, style.Strikethrough, Link: style.Link);

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
