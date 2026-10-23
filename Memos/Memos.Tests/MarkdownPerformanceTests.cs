using Markdig;
using Memos.Core.Helpers;
using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.ViewModels;
using System.Text;

namespace Memos.Tests;

/// <summary>A document with every kind of block, repeated to the requested size.</summary>
internal static class SampleMarkdown
{
    public static string Build(int sections, bool code = true)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < sections; i++)
        {
            sb.Append("## Sección ").Append(i).Append("\n\n")
              .Append("Texto con **negrita**, *cursiva*, ~~tachado~~, `código`, un [enlace](https://example.com/").Append(i)
              .Append("), https://example.com/auto y una nota[^n").Append(i).Append("].\n\n")
              .Append("> Una cita con *énfasis*.\n\n- uno\n- dos\n  - anidado\n- [x] tarea\n\n")
              .Append("| A | B |\n|---|--:|\n| 1 | 2 |\n\n");
            if (code)
            {
                sb.Append("```csharp\nvar s").Append(i % 3).Append(" = \"hola\"; // comentario\n```\n\n");
            }

            sb.Append("[^n").Append(i).Append("]: Nota ").Append(i).Append(".\n\n");
        }

        return sb.ToString();
    }
}

public class MarkdownEngineTests
{
    private readonly MarkdownService _markdown = new();

    [Fact]
    public void Blocks_put_together_are_exactly_what_the_parser_renders_for_the_whole_document()
    {
        // A paragraph far bigger than the render buffer makes it grow in the middle of a block.
        var text = SampleMarkdown.Build(30) + string.Join(' ', Enumerable.Repeat("palabra *larga*", 2000)) + "\n\n    código indentado\n";

        var blocks = _markdown.Analyze(text, new MarkdownRenderOptions(CodeHighlighting: false)).Html();

        Assert.Equal(Markdown.ToHtml(text, MarkdownService.Pipeline), blocks);
    }

    [Fact]
    public void Analysing_twice_gives_the_same_result()
    {
        var text = "---\ntitle: t\n---\n" + SampleMarkdown.Build(10);
        var options = new MarkdownRenderOptions(CodeHighlighting: true, RelativeUrlBase: "https://memos-doc.example/", IncludeSyntaxRuns: true);

        var first = _markdown.Analyze(text, options);
        var second = _markdown.Analyze(text, options);

        Assert.Equal(first.Blocks, second.Blocks);
        Assert.Equal(first.Headings, second.Headings);
        Assert.Equal(first.Runs, second.Runs);
        Assert.Contains("<div class=\"code\">", first.Html());
    }

    [Fact]
    public void Text_after_a_neutralised_autolink_is_still_analysed()
    {
        const string text = "a <javascript:alert(1)> b <https://ok.example> c **d**";
        var analysis = _markdown.Analyze(text, new MarkdownRenderOptions(IncludeSyntaxRuns: true));
        var html = analysis.Html();

        Assert.DoesNotContain("href=\"javascript", html);
        Assert.Contains("javascript:alert(1)", html);
        Assert.Contains("<a href=\"https://ok.example\">", html);
        Assert.Contains(analysis.Runs, r => r.Color == SyntaxColor.Link && text.Substring(r.Start, r.Length) == "<https://ok.example>");
        Assert.Contains(analysis.Runs, r => r.Flags == SyntaxFlags.Bold && text.Substring(r.Start, r.Length) == "**d**");
    }

    [Fact]
    public void A_cancelled_analysis_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _markdown.Analyze(SampleMarkdown.Build(5), new MarkdownRenderOptions(), cancelled.Token));
    }

    [Fact]
    public void Runs_of_a_long_document_are_adjacent_or_apart_but_never_overlap()
    {
        var text = SampleMarkdown.Build(200);
        var runs = _markdown.Analyze(text, new MarkdownRenderOptions(IncludeSyntaxRuns: true)).Runs;

        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.True(r.Length > 0 && r.End <= text.Length));
        for (var i = 1; i < runs.Count; i++)
        {
            Assert.True(runs[i - 1].End <= runs[i].Start);

            // Two runs that touch must differ, or they would be one run.
            Assert.True(runs[i - 1].End < runs[i].Start || (runs[i - 1].Color, runs[i - 1].Flags) != (runs[i].Color, runs[i].Flags));
        }
    }

    [Fact]
    public void Warm_up_runs_without_a_document()
    {
        MarkdownService.WarmUp();
    }

    [Fact]
    public void Highlighted_code_is_only_computed_once()
    {
        // With the highlighter remembering its work, a second pass with highlighting must not cost
        // much more memory than one without it. Before, it cost about three times as much.
        var text = SampleMarkdown.Build(150);
        var highlighted = new MarkdownRenderOptions(CodeHighlighting: true);
        var plain = new MarkdownRenderOptions(CodeHighlighting: false);
        _markdown.Analyze(text, highlighted);
        _markdown.Analyze(text, plain);

        var withHighlighting = Allocated(() => _markdown.Analyze(text, highlighted));
        var without = Allocated(() => _markdown.Analyze(text, plain));

        Assert.True(withHighlighting < without * 1.3, $"{withHighlighting} bytes with highlighting, {without} without");
    }

    [Fact]
    public void Analysis_grows_in_step_with_the_document()
    {
        // Memory is used as the measure because, unlike time, it does not depend on the machine.
        var options = new MarkdownRenderOptions(CodeHighlighting: true, IncludeSyntaxRuns: true);
        var small = SampleMarkdown.Build(100);
        var large = SampleMarkdown.Build(800);
        _markdown.Analyze(large, options);

        var smallCost = Allocated(() => _markdown.Analyze(small, options));
        var largeCost = Allocated(() => _markdown.Analyze(large, options));

        Assert.True(largeCost < smallCost * 8 * 1.5, $"{smallCost} bytes for 100 sections, {largeCost} for 800");
    }

    private static long Allocated(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

public class CodeHighlighterTests
{
    [Fact]
    public void The_same_code_is_served_from_memory()
    {
        var language = CodeHighlighter.FindLanguage("csharp")!;
        var code = "var único = " + Guid.NewGuid().ToString("N") + ";";

        var first = CodeHighlighter.Highlight(language, code);
        var second = CodeHighlighter.Highlight(language, new string(code));

        Assert.Same(first, second);
        Assert.Contains("<span", first);
    }

    [Fact]
    public void The_same_code_in_another_language_is_another_entry()
    {
        const string code = "var x = 1; // nota";

        var csharp = CodeHighlighter.Highlight(CodeHighlighter.FindLanguage("cs")!, code);
        var python = CodeHighlighter.Highlight(CodeHighlighter.FindLanguage("py")!, code);

        Assert.NotEqual(csharp, python);
    }

    [Theory]
    [InlineData("csharp", "c#")]
    [InlineData(" CS ", "c#")]
    [InlineData("js", "javascript")]
    [InlineData("ts", "typescript")]
    [InlineData("py", "python")]
    [InlineData("xaml", "xml")]
    public void Common_aliases_name_their_language(string info, string id)
    {
        Assert.Equal(id, CodeHighlighter.FindLanguage(info)?.Id, ignoreCase: true);
        Assert.Same(CodeHighlighter.FindLanguage(info), CodeHighlighter.FindLanguage(info));
    }

    [Theory]
    [InlineData("nolang")]
    [InlineData("")]
    [InlineData("{.weird attr=1}")]
    public void Unknown_info_strings_name_no_language(string info)
    {
        Assert.Null(CodeHighlighter.FindLanguage(info));
        Assert.Null(CodeHighlighter.FindLanguage(info));
    }

    [Fact]
    public void Memory_is_bounded_and_what_is_still_in_use_survives()
    {
        var language = CodeHighlighter.FindLanguage("csharp")!;
        var budget = CodeHighlighter.GenerationBudget;
        try
        {
            CodeHighlighter.GenerationBudget = 2_000;
            var kept = "var kept = \"" + Guid.NewGuid().ToString("N") + "\";";
            var keptHtml = CodeHighlighter.Highlight(language, kept);
            var forgotten = "var forgotten = \"" + Guid.NewGuid().ToString("N") + "\";";
            var forgottenHtml = CodeHighlighter.Highlight(language, forgotten);

            // Enough new entries to turn the generations over several times; one is asked for throughout.
            for (var i = 0; i < 200; i++)
            {
                CodeHighlighter.Highlight(language, "var filler" + i + " = \"" + new string('x', 200) + "\";");
                Assert.Same(keptHtml, CodeHighlighter.Highlight(language, kept));
            }

            Assert.True(CodeHighlighter.CachedEntries < 50, $"{CodeHighlighter.CachedEntries} entries");
            var again = CodeHighlighter.Highlight(language, forgotten);
            Assert.NotSame(forgottenHtml, again);
            Assert.Equal(forgottenHtml, again);
        }
        finally
        {
            CodeHighlighter.GenerationBudget = budget;
        }
    }

    [Fact]
    public async Task Highlighting_from_several_threads_gives_the_same_html()
    {
        var language = CodeHighlighter.FindLanguage("js")!;
        var codes = Enumerable.Range(0, 40).Select(i => "function f" + i + "(x) { return x * " + i + "; }").ToArray();
        var expected = codes.Select(c => CodeHighlighter.Highlight(language, c)).ToArray();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            codes.Select(c => CodeHighlighter.Highlight(language, c)).ToArray())));

        Assert.All(results, r => Assert.Equal(expected, r));
    }
}

public class AnalysisSchedulingTests
{
    [Fact]
    public async Task The_pause_before_an_analysis_follows_how_long_the_last_one_took()
    {
        // Nothing has been analysed in a plain text document, so only the size can be used.
        var unmeasured = TestHelpers.NewDocument();
        Assert.Equal(DocumentViewModel.AnalysisDelay, unmeasured.GetAnalysisDelay(1_000));
        Assert.Equal(DocumentViewModel.MaxAnalysisDelay, unmeasured.GetAnalysisDelay(500_000));

        var document = TestHelpers.NewDocument(DocumentKind.Markdown);
        document.ApplyEdit(new TextEdit(0, 0, "# Uno\n\ntexto", 0, 0));
        await document.PendingAnalysis;

        // Once measured, the size no longer decides: a quick analysis is repeated promptly.
        var delay = document.GetAnalysisDelay(500_000);
        Assert.True(delay > DocumentViewModel.AnalysisDelay);
        Assert.True(delay <= DocumentViewModel.MaxAnalysisDelay);
        Assert.Equal(delay, document.GetAnalysisDelay(10));
    }

    [Fact]
    public async Task The_outline_is_kept_while_it_reads_the_same_and_still_points_to_the_right_line()
    {
        var document = TestHelpers.NewDocument(DocumentKind.Markdown);
        document.ApplyEdit(new TextEdit(0, 0, "intro\n\n# Uno\n\n## Dos", 0, 0));
        await document.PendingAnalysis;
        var outline = document.Headings;
        var changes = 0;
        document.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(DocumentViewModel.Headings) ? 1 : 0;

        document.ApplyEdit(new TextEdit(0, 0, "más\n\n", 0, 0));
        await document.PendingAnalysis;

        Assert.Same(outline, document.Headings);
        Assert.Equal(0, changes);
        Assert.Equal(2, outline[0].Line);
        Assert.Equal(4, document.CurrentHeading(outline[0]).Line);
        Assert.Equal(6, document.CurrentHeading(outline[1]).Line);
        Assert.Equal([4, 6], document.Analysis!.Headings.Select(h => h.Line));

        document.ApplyEdit(new TextEdit(document.Text.Length, 0, " y medio", 0, 0));
        await document.PendingAnalysis;

        Assert.NotSame(outline, document.Headings);
        Assert.Equal(1, changes);
        Assert.Equal(["Uno", "Dos y medio"], document.Headings.Select(h => h.Text));
        Assert.Equal(6, document.CurrentHeading(document.Headings[1]).Line);
    }

    [Fact]
    public async Task A_heading_that_is_no_longer_in_the_outline_is_returned_as_it_was()
    {
        var document = TestHelpers.NewDocument(DocumentKind.Markdown);
        document.ApplyEdit(new TextEdit(0, 0, "# Uno", 0, 0));
        await document.PendingAnalysis;
        var stale = new HeadingInfo(1, "Otro", 7, 20, null);

        Assert.Same(stale, document.CurrentHeading(stale));
    }
}
