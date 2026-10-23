using Memos.Core.Markdown;

namespace Memos.Tests;

/// <summary>What the printer receives for a Markdown document: blocks of styled text, never its syntax.</summary>
public class MarkdownPrintTests
{
    private static string Text(PrintBlock block) => string.Concat(block.Spans.Select(s => s.Text));

    [Fact]
    public void Headings_and_paragraphs_keep_their_inline_styles()
    {
        var blocks = MarkdownPrintBuilder.Build("## Título *uno*\n\nnormal **negrita** *cursiva* ~~tachado~~ `código` [enlace](https://example.com) fin");

        Assert.Equal([PrintBlockKind.Heading, PrintBlockKind.Paragraph], blocks.Select(b => b.Kind));
        Assert.Equal(2, blocks[0].Level);
        Assert.Equal([new PrintSpan("Título "), new PrintSpan("uno", Italic: true)], blocks[0].Spans);
        Assert.Equal(
            [
                new PrintSpan("normal "), new PrintSpan("negrita", Bold: true), new PrintSpan(" "),
                new PrintSpan("cursiva", Italic: true), new PrintSpan(" "), new PrintSpan("tachado", Strikethrough: true),
                new PrintSpan(" "), new PrintSpan("código", Code: true), new PrintSpan(" "),
                new PrintSpan("enlace", Link: true) { LinkUrl = "https://example.com" }, new PrintSpan(" fin"),
            ],
            blocks[1].Spans);
    }

    [Fact]
    public void Styles_nest_and_line_breaks_are_kept_only_when_they_are_hard()
    {
        var blocks = MarkdownPrintBuilder.Build("***todo*** y [**enlace fuerte**](x)\nmisma línea  \notra línea &amp; más");

        var block = Assert.Single(blocks);
        Assert.Equal(new PrintSpan("todo", Bold: true, Italic: true), block.Spans[0]);
        Assert.Contains(new PrintSpan("enlace fuerte", Bold: true, Link: true) { LinkUrl = "x" }, block.Spans);
        Assert.Equal("todo y enlace fuerte misma línea\notra línea & más", Text(block));
    }

    [Fact]
    public void Lists_become_indented_blocks_with_their_markers()
    {
        var blocks = MarkdownPrintBuilder.Build("- uno\n- dos\n  1. a\n  2. b\n\n     segundo párrafo\n- tres\n\n7. siete\n8. ocho");

        Assert.Equal(["uno", "dos", "a", "b", "segundo párrafo", "tres", "siete", "ocho"], blocks.Select(Text));
        Assert.Equal(["•", "•", "1.", "2.", null, "•", "7.", "8."], blocks.Select(b => b.Marker));
        Assert.Equal([1, 1, 2, 2, 2, 1, 1, 1], blocks.Select(b => b.Indent));
    }

    [Fact]
    public void Task_lists_use_a_check_box_as_marker_and_empty_items_keep_theirs()
    {
        var blocks = MarkdownPrintBuilder.Build("- [ ] pendiente\n- [x] hecha\n-\n- normal");

        Assert.Equal(["☐", "☑", "•", "•"], blocks.Select(b => b.Marker));
        Assert.Equal(["pendiente", "hecha", "", "normal"], blocks.Select(Text));
    }

    [Fact]
    public void Quotes_code_and_rules_are_told_apart()
    {
        var blocks = MarkdownPrintBuilder.Build("> cita\n>\n> > anidada\n\n```cs\nvar x = 1;\n\tvar y = 2;\n```\n\n---\n\n    sangrado");

        Assert.Equal(
            [PrintBlockKind.Paragraph, PrintBlockKind.Paragraph, PrintBlockKind.Code, PrintBlockKind.Rule, PrintBlockKind.Code],
            blocks.Select(b => b.Kind));
        Assert.Equal([1, 2, 0, 0, 0], blocks.Select(b => b.QuoteDepth));
        Assert.Equal("var x = 1;\n\tvar y = 2;", Text(blocks[2]));
        Assert.All(blocks[2].Spans, span => Assert.True(span.Code));
        Assert.Equal("sangrado", Text(blocks[4]));
    }

    [Fact]
    public void A_table_keeps_its_rows_alignment_and_cell_styles()
    {
        var blocks = MarkdownPrintBuilder.Build("| a | b | c |\n|:--|:-:|--:|\n| 1 | **2** | 3 |\n| solo |");

        var table = Assert.Single(blocks);
        Assert.Equal(PrintBlockKind.Table, table.Kind);
        Assert.Equal(3, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(3, row.Count));
        Assert.Equal([true, true, true], table.Rows[0].Select(c => c.IsHeader));
        Assert.Equal([false, false, false], table.Rows[1].Select(c => c.IsHeader));
        Assert.Equal([PrintAlignment.Left, PrintAlignment.Center, PrintAlignment.Right], table.Rows[1].Select(c => c.Alignment));
        Assert.Equal([new PrintSpan("2", Bold: true)], table.Rows[1][1].Spans);
        Assert.Equal(["solo", "", ""], table.Rows[2].Select(c => string.Concat(c.Spans.Select(s => s.Text))));
    }

    [Fact]
    public void Images_carry_their_address_and_alternative_text_unless_the_address_is_unsafe()
    {
        var blocks = MarkdownPrintBuilder.Build("![el *logo*](img/logo.png) y ![malo](javascript:alert(1)) y ![svg](data:image/svg+xml;base64,AAAA)");

        var spans = Assert.Single(blocks).Spans;
        Assert.Equal(new PrintSpan("el logo", ImageUrl: "img/logo.png"), spans[0]);
        Assert.Equal(["img/logo.png"], spans.Select(s => s.ImageUrl).OfType<string>());
        Assert.Contains("malo", Text(blocks[0]));
        Assert.Contains("svg", Text(blocks[0]));
    }

    [Fact]
    public void Front_matter_and_link_definitions_are_left_out_and_footnotes_go_to_the_end()
    {
        var blocks = MarkdownPrintBuilder.Build("---\ntitle: Secreto\n---\n\nTexto[^n] con [ref].\n\n[ref]: https://example.com\n[^n]: La **nota**.");

        Assert.Equal([PrintBlockKind.Paragraph, PrintBlockKind.Rule, PrintBlockKind.Paragraph], blocks.Select(b => b.Kind));
        Assert.Equal("Texto[1] con ref.", Text(blocks[0]));
        Assert.Equal("1.", blocks[2].Marker);
        Assert.Equal("La nota.", Text(blocks[2]));
        Assert.DoesNotContain(blocks, b => Text(b).Contains("Secreto") || Text(b).Contains("example.com"));
    }

    [Fact]
    public void Raw_html_is_printed_as_the_text_it_is()
    {
        var blocks = MarkdownPrintBuilder.Build("<script>alert(1)</script>\n\nhola <b>mundo</b>");

        Assert.Equal(["<script>alert(1)</script>", "hola <b>mundo</b>"], blocks.Select(Text));
        Assert.All(blocks, b => Assert.Equal(PrintBlockKind.Paragraph, b.Kind));
    }

    [Fact]
    public void Blocks_know_the_source_line_they_start_at()
    {
        var blocks = MarkdownPrintBuilder.Build("# A\n\npárrafo\n\n- uno\n- dos\n\n| a |\n|---|\n| 1 |");

        Assert.Equal([0, 2, 4, 5, 7], blocks.Select(b => b.Line));
    }

    [Fact]
    public void Unsafe_links_are_shown_but_go_nowhere()
    {
        var spans = Assert.Single(MarkdownPrintBuilder.Build("[a](javascript:alert(1)) [b](docs/otro.md) <c@example.com>")).Spans;

        Assert.Equal([null, "docs/otro.md", "mailto:c@example.com"], spans.Where(s => s.Link).Select(s => s.LinkUrl));
    }

    [Fact]
    public void Code_with_a_known_language_is_split_into_tokens_only_when_asked()
    {
        const string source = "```cs\nvar x = \"s\"; // c\n```";

        var plain = Assert.Single(MarkdownPrintBuilder.Build(source));
        var highlighted = Assert.Single(MarkdownPrintBuilder.Build(source, highlightCode: true));

        Assert.All(plain.Spans, span => Assert.Equal(CodeToken.None, span.Token));
        Assert.Equal(Text(plain), Text(highlighted));
        Assert.Contains(new PrintSpan("var", Code: true) { Token = CodeToken.Keyword }, highlighted.Spans);
        Assert.Contains(highlighted.Spans, span => span.Token == CodeToken.String && span.Text == "\"s\"");
        Assert.Contains(highlighted.Spans, span => span.Token == CodeToken.Comment && span.Text.StartsWith("// c", StringComparison.Ordinal));
    }

    [Fact]
    public void The_preview_shows_the_front_matter_as_a_table()
    {
        var analysis = new MarkdownService().Analyze("---\ntitle: Hola\n---\ntexto", new MarkdownRenderOptions(IncludeHtml: false, IncludeNative: true));

        Assert.Empty(analysis.Blocks);
        Assert.Equal("Hola", analysis.FrontMatter!["title"]);
        Assert.Equal([PrintBlockKind.Table, PrintBlockKind.Paragraph], analysis.Native.Select(b => b.Kind));
        Assert.Equal(["title", "Hola"], analysis.Native[0].Rows[0].Select(c => string.Concat(c.Spans.Select(s => s.Text))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("---\ntitle: x\n---\n")]
    public void A_document_without_content_has_no_blocks(string text) =>
        Assert.Empty(MarkdownPrintBuilder.Build(text));

    [Fact]
    public void Damaged_or_odd_markdown_never_throws()
    {
        const string sample = "# t\n- [x] a\n  > b\n  ```\n  c\n| a |\n|--|\n| ![i](u) |\n[^1]: n\n***\n1. \n   - \n[^1] <x> &amp; ~~s~~ **b *i `c`";
        for (var length = 0; length <= sample.Length; length++)
        {
            _ = MarkdownPrintBuilder.Build(sample[..length]);
        }
    }
}
