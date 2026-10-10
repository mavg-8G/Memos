using Memos.Core.Helpers;
using Memos.Core.Markdown;
using Memos.Core.Models;

namespace Memos.Tests;

public class MarkdownRenderingTests
{
    private readonly MarkdownService _markdown = new();

    private string Render(string text, bool codeHighlighting = false) =>
        _markdown.Analyze(text, new MarkdownRenderOptions(codeHighlighting)).Html();

    [Fact]
    public void Renders_the_core_syntax()
    {
        var html = Render("""
            # Título

            Texto con **negrita**, *cursiva*, ~~tachado~~ y `código`.

            > Una cita

            1. uno
            2. dos

            - a
            - b

            ---

            [enlace](https://example.com) ![alt](imagen.png)
            """);

        Assert.Contains("<h1 id=\"título\">Título</h1>", html);
        Assert.Contains("<strong>negrita</strong>", html);
        Assert.Contains("<em>cursiva</em>", html);
        Assert.Contains("<del>tachado</del>", html);
        Assert.Contains("<code>código</code>", html);
        Assert.Contains("<blockquote>", html);
        Assert.Contains("<ol>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<hr />", html);
        Assert.Contains("<a href=\"https://example.com\">enlace</a>", html);
        Assert.Contains("<img src=\"imagen.png\" alt=\"alt\" />", html);
    }

    [Fact]
    public void Renders_github_flavoured_extensions()
    {
        var html = Render("""
            | A | B |
            |---|--:|
            | 1 | 2 |

            - [x] hecha
            - [ ] pendiente

            Visita https://example.com/ruta ahora.

            Una nota[^1].

            [^1]: El texto de la nota.
            """);

        Assert.Contains("<table>", html);
        Assert.Contains("<th>A</th>", html);
        Assert.Contains("text-align: right", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("checked=\"checked\"", html);
        Assert.Contains("<a href=\"https://example.com/ruta\">", html);
        Assert.Contains("class=\"footnotes\"", html);
        Assert.Contains("El texto de la nota.", html);
    }

    [Fact]
    public void All_six_heading_levels_appear_in_the_outline_with_their_lines()
    {
        var analysis = _markdown.Analyze("# Uno\n\ntexto\n\n## Dos *con* `formato`\n###### Seis", new MarkdownRenderOptions());

        Assert.Equal([(1, "Uno", 0), (2, "Dos con formato", 4), (6, "Seis", 5)],
            analysis.Headings.Select(h => (h.Level, h.Text, h.Line)));
        Assert.Equal("dos-con-formato", analysis.Headings[1].Id);
    }

    [Fact]
    public void Blocks_carry_their_source_line_for_scroll_sync()
    {
        var analysis = _markdown.Analyze("# A\n\npárrafo\n\n- lista\n", new MarkdownRenderOptions());

        Assert.Equal([0, 2, 4], analysis.Blocks.Select(b => b.StartLine));
        Assert.Equal("<section data-line=\"2\"><p>párrafo</p>\n</section>", HtmlDocumentBuilder.WrapBlock(analysis.Blocks[1]));
    }

    [Fact]
    public void Fenced_code_is_highlighted_for_known_languages_only()
    {
        var known = Render("```csharp\nvar x = \"hola\";\n```", codeHighlighting: true);
        var unknown = Render("```nolang\n<b>x</b>\n```", codeHighlighting: true);
        var disabled = Render("```csharp\nvar x = 1;\n```");

        Assert.Contains("<div class=\"code\">", known);
        Assert.Contains("<span", known);
        Assert.DoesNotContain("<span", unknown);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", unknown);
        Assert.DoesNotContain("<span", disabled);
    }

    [Fact]
    public void Relative_urls_are_rebased_only_when_a_base_is_given()
    {
        const string source = "![a](img/a.png) [b](otro.md) [c](https://example.com) [d](#ancla)";
        var rebased = _markdown.Analyze(source, new MarkdownRenderOptions(RelativeUrlBase: "https://memos-doc.example/")).Html();

        Assert.Contains("src=\"https://memos-doc.example/img/a.png\"", rebased);
        Assert.Contains("href=\"https://memos-doc.example/otro.md\"", rebased);
        Assert.Contains("href=\"https://example.com\"", rebased);
        Assert.Contains("href=\"#ancla\"", rebased);
        Assert.Contains("src=\"img/a.png\"", Render(source));
    }
}

public class MarkdownSecurityTests
{
    private readonly MarkdownService _markdown = new();

    private string Render(string text) => _markdown.Analyze(text, new MarkdownRenderOptions()).Html();

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("texto <img src=x onerror=alert(1)> más")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<div onclick=\"alert(1)\">x</div>")]
    [InlineData("<style>body{display:none}</style>")]
    public void Raw_html_is_shown_as_text_never_emitted(string source)
    {
        var html = Render(source);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<div", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;", html);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[x]: javascript:alert(1)\n\n[x]")]
    [InlineData("![x](javascript:alert(1))")]
    [InlineData("![x](data:image/svg+xml;base64,PHN2Zy8+)")]
    public void Dangerous_url_schemes_are_neutralised(string source)
    {
        var html = Render(source);

        Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"vbscript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"data:image/svg", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.com", false, true)]
    [InlineData("mailto:a@example.com", false, true)]
    [InlineData("carpeta/archivo.md", false, true)]
    [InlineData("#seccion", false, true)]
    [InlineData("C:/docs/a.png", true, true)]
    [InlineData("data:image/png;base64,AAAA", true, true)]
    [InlineData("data:image/png;base64,AAAA", false, false)]
    [InlineData(" javascript:alert(1)", false, false)]
    [InlineData("ms-settings:privacy", false, false)]
    public void Url_allow_list(string url, bool isImage, bool expected)
    {
        Assert.Equal(expected, MarkdownService.IsSafeUrl(url, isImage));
    }

    [Fact]
    public void Exported_page_forbids_scripts_and_escapes_the_title()
    {
        var analysis = _markdown.Analyze("# Hola\n\n<script>alert(1)</script>", new MarkdownRenderOptions());
        var page = HtmlDocumentBuilder.BuildPage("<Título> & \"más\"", HtmlDocumentBuilder.WrapBlocks(analysis.Blocks), new HtmlPageOptions());

        Assert.StartsWith("<!DOCTYPE html>", page);
        Assert.Contains("<meta charset=\"utf-8\">", page);
        Assert.Contains("default-src 'none'", page);
        Assert.DoesNotContain("script-src", page);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title>&lt;Título&gt; &amp; &quot;más&quot;</title>", page);
        Assert.Contains("prefers-color-scheme: dark", page);
        Assert.Contains("<h1 id=\"hola\">Hola</h1>", page);
    }

    [Fact]
    public void Preview_page_only_loads_remote_images_when_allowed()
    {
        var blocked = HtmlDocumentBuilder.BuildPage("t", "", new HtmlPageOptions { Dark = true, LocalResourceOrigin = "https://memos-doc.example" });
        var allowed = HtmlDocumentBuilder.BuildPage("t", "", new HtmlPageOptions { Dark = false, LocalResourceOrigin = "https://memos-doc.example", AllowRemoteImages = true });

        Assert.Contains("img-src https://memos-doc.example data:;", blocked);
        Assert.Contains("img-src https://memos-doc.example data: https: http:;", allowed);
        Assert.Contains("color-scheme:dark", blocked);
        Assert.Contains("color-scheme:light", allowed);
    }
}

public class MarkdownSyntaxRunTests
{
    private readonly MarkdownService _markdown = new();

    private IReadOnlyList<SyntaxRun> Runs(string text) =>
        _markdown.Analyze(text, new MarkdownRenderOptions(IncludeSyntaxRuns: true)).Runs;

    [Fact]
    public void Runs_cover_the_styled_source_and_never_overlap()
    {
        const string text = "# Título\n\nTexto **negrita** y `código` y [enlace](https://example.com).\n\n> cita\n\n- item";
        var runs = Runs(text);

        Assert.Contains(runs, r => r.Color == SyntaxColor.Heading && r.Flags == SyntaxFlags.Bold && text.Substring(r.Start, r.Length) == "# Título");
        Assert.Contains(runs, r => r.Flags == SyntaxFlags.Bold && r.Color == SyntaxColor.None && text.Substring(r.Start, r.Length) == "**negrita**");
        Assert.Contains(runs, r => r.Color == SyntaxColor.Code && text.Substring(r.Start, r.Length) == "`código`");
        Assert.Contains(runs, r => r.Color == SyntaxColor.Link && text.Substring(r.Start, r.Length) == "[enlace](https://example.com)");
        Assert.Contains(runs, r => r.Color == SyntaxColor.Marker && text.Substring(r.Start, r.Length) == "-");

        for (var i = 1; i < runs.Count; i++)
        {
            Assert.True(runs[i - 1].End <= runs[i].Start);
        }
    }

    [Fact]
    public void Nested_styles_combine()
    {
        const string text = "# Un *título*";
        var inner = Runs(text).Single(r => text.Substring(r.Start, r.Length) == "*título*");

        Assert.Equal(SyntaxColor.Heading, inner.Color);
        Assert.Equal(SyntaxFlags.Bold | SyntaxFlags.Italic, inner.Flags);
    }

    [Fact]
    public void Runs_are_skipped_unless_requested()
    {
        Assert.Empty(_markdown.Analyze("# x", new MarkdownRenderOptions()).Runs);
    }

    [Fact]
    public void Typing_inside_a_paragraph_only_dirties_the_edit()
    {
        const string before = "# Título\n\nUn párrafo normal.\n\n`código`";
        var after = before.Insert(14, "X");

        var dirty = HighlightPlanner.GetDirtyRange(Runs(before), Runs(after), TextDiff.Compute(before, after), after.Length);

        Assert.Equal((14, 15), dirty);
    }

    [Fact]
    public void Opening_a_code_fence_dirties_everything_it_swallows()
    {
        const string before = "texto\n\n**negrita**\n\nfin";
        var after = "```\n" + before;

        var (start, end) = HighlightPlanner.GetDirtyRange(Runs(before), Runs(after), TextDiff.Compute(before, after), after.Length);

        Assert.Equal(0, start);
        Assert.True(end >= after.IndexOf("**negrita**", StringComparison.Ordinal) + 11);
    }
}

public class MarkdownFormattingTests
{
    private static string Apply(string text, TextEdit? edit) => edit!.Value.Apply(text);

    [Fact]
    public void Bold_wraps_the_selection_and_toggles_off_again()
    {
        const string text = "hola mundo";
        var on = MarkdownFormatting.ToggleInline(text, 5, 5, "**", "texto");
        var wrapped = on.Apply(text);
        var off = MarkdownFormatting.ToggleInline(wrapped, on.SelectionStart, on.SelectionLength, "**", "texto");

        Assert.Equal("hola **mundo**", wrapped);
        Assert.Equal("mundo", wrapped.Substring(on.SelectionStart, on.SelectionLength));
        Assert.Equal(text, off.Apply(wrapped));
    }

    [Fact]
    public void Inline_markers_stay_tight_around_the_words()
    {
        var edit = MarkdownFormatting.ToggleInline("a  palabra  b", 2, 9, "*", "texto");

        Assert.Equal("a  *palabra*  b", edit.Apply("a  palabra  b"));
    }

    [Fact]
    public void With_no_selection_a_placeholder_is_inserted_and_selected()
    {
        var edit = MarkdownFormatting.ToggleInline("", 0, 0, "~~", "texto");

        Assert.Equal("~~texto~~", edit.Apply(""));
        Assert.Equal((2, 5), (edit.SelectionStart, edit.SelectionLength));
    }

    [Fact]
    public void Heading_level_replaces_any_existing_one()
    {
        Assert.Equal("## Título", Apply("Título", MarkdownFormatting.SetHeading("Título", 3, 0, 2)));
        Assert.Equal("### Título", Apply("# Título", MarkdownFormatting.SetHeading("# Título", 3, 0, 3)));
        Assert.Equal("Título", Apply("###### Título", MarkdownFormatting.SetHeading("###### Título", 0, 0, 0)));
        Assert.Null(MarkdownFormatting.SetHeading("Título", 0, 0, 0));
    }

    [Fact]
    public void Lists_and_quotes_toggle_per_line_and_convert_between_kinds()
    {
        const string text = "uno\ndos\n\ntres";

        var bullets = Apply(text, MarkdownFormatting.ToggleBulletList(text, 0, text.Length));
        var numbered = Apply(bullets, MarkdownFormatting.ToggleNumberedList(bullets, 0, bullets.Length));
        var tasks = Apply(numbered, MarkdownFormatting.ToggleTaskList(numbered, 0, numbered.Length));
        var plain = Apply(tasks, MarkdownFormatting.ToggleTaskList(tasks, 0, tasks.Length));

        Assert.Equal("- uno\n- dos\n\n- tres", bullets);
        Assert.Equal("1. uno\n2. dos\n\n3. tres", numbered);
        Assert.Equal("- [ ] uno\n- [ ] dos\n\n- [ ] tres", tasks);
        Assert.Equal(text, plain);
        Assert.Equal("> uno", Apply("uno", MarkdownFormatting.ToggleQuote("uno", 0, 0)));
        Assert.Equal("uno", Apply("> uno", MarkdownFormatting.ToggleQuote("> uno", 2, 0)));
    }

    [Fact]
    public void Links_and_images_escape_what_would_break_the_syntax()
    {
        Assert.Equal("[a \\[b\\]](https://example.com)", Apply("", MarkdownFormatting.InsertLink("", 0, 0, "a [b]", "https://example.com")));
        Assert.Equal("![foto](<mis fotos/a (1).png>)", Apply("", MarkdownFormatting.InsertImage("", 0, 0, "foto", "mis fotos\\a (1).png")));
    }

    [Fact]
    public void Blocks_are_separated_from_surrounding_text_by_blank_lines()
    {
        const string text = "antes\ndespués";

        Assert.Equal("antes\n\n---\n\ndespués", Apply(text, MarkdownFormatting.InsertHorizontalRule(text, 5, 0)));
        Assert.Equal("antes\n\n```\n\n```\n\ndespués", Apply(text, MarkdownFormatting.InsertCodeBlock(text, 5, 0)));
        Assert.Equal("```\nantes\ndespués\n```", Apply(text, MarkdownFormatting.InsertCodeBlock(text, 0, text.Length)));
    }

    [Fact]
    public void Table_template_is_valid_markdown_with_the_requested_size()
    {
        var table = Apply("", MarkdownFormatting.InsertTable("", 0, 0, rows: 2, columns: 3, "Col"));
        var html = new MarkdownService().Analyze(table, new MarkdownRenderOptions()).Html();

        Assert.Equal("| Col 1 | Col 2 | Col 3 |\n| --- | --- | --- |\n|     |     |     |\n|     |     |     |\n", table);
        Assert.Equal(3, html.Split("<th>").Length - 1);
        Assert.Equal(3, html.Split("<tr>").Length - 1);
    }
}
