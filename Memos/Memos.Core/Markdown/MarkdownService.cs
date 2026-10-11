using System.Text;
using ColorCode;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Memos.Core.Markdown;

/// <summary>One top-level block of the document rendered to HTML, with the zero-based source line it starts at.</summary>
public sealed record MarkdownBlock(string Html, int StartLine);

public sealed record HeadingInfo(int Level, string Text, int Line, int Offset, string? Id);

public sealed record MarkdownAnalysis(
    IReadOnlyList<MarkdownBlock> Blocks,
    IReadOnlyList<HeadingInfo> Headings,
    IReadOnlyList<SyntaxRun> Runs,
    FrontMatter? FrontMatter = null);

/// <param name="IncludeFrontMatter">Show the YAML front matter as a metadata block (preview) or leave it out (export).</param>
public sealed record MarkdownRenderOptions(
    bool CodeHighlighting = true,
    string? RelativeUrlBase = null,
    bool IncludeSyntaxRuns = false,
    bool IncludeFrontMatter = true);

public interface IMarkdownService
{
    /// <summary>
    /// Parses '\n'-separated Markdown once and produces the preview blocks, the outline and
    /// (optionally) the styled runs for the editor.
    /// </summary>
    MarkdownAnalysis Analyze(string text, MarkdownRenderOptions options, CancellationToken cancellationToken = default);
}

public sealed class MarkdownService : IMarkdownService
{
    // Raw HTML is disabled on purpose: whatever the document contains is shown as text, never executed.
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseFootnotes()
        .UseYamlFrontMatter()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UsePreciseSourceLocation()
        .DisableHtml()
        .Build();

    public MarkdownAnalysis Analyze(string text, MarkdownRenderOptions options, CancellationToken cancellationToken = default)
    {
        var document = Markdig.Markdown.Parse(text, Pipeline);
        cancellationToken.ThrowIfCancellationRequested();

        var painter = options.IncludeSyntaxRuns ? new SyntaxPainter(text.Length) : null;
        var headings = new List<HeadingInfo>();
        Visit(document, painter, headings, text);
        cancellationToken.ThrowIfCancellationRequested();

        var writer = new StringWriter(new StringBuilder(1024));
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        if (options.RelativeUrlBase is { } baseUrl)
        {
            renderer.LinkRewriter = url => IsRelativeUrl(url) ? baseUrl + url.TrimStart('/') : url;
        }

        if (options.CodeHighlighting)
        {
            var original = renderer.ObjectRenderers.FindExact<CodeBlockRenderer>();
            if (original is not null)
            {
                renderer.ObjectRenderers.Replace<CodeBlockRenderer>(new HighlightingCodeBlockRenderer(original));
            }
        }

        var blocks = new List<MarkdownBlock>(document.Count);
        var builder = writer.GetStringBuilder();
        FrontMatter? frontMatter = null;
        foreach (var block in document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block is YamlFrontMatterBlock yaml)
            {
                // Metadata, not content: shown as its own block and never rendered as Markdown.
                frontMatter = FrontMatter.Parse(yaml.Lines.ToString());
                if (options.IncludeFrontMatter)
                {
                    blocks.Add(new MarkdownBlock(frontMatter.ToHtml(), block.Line));
                }

                continue;
            }

            builder.Clear();
            renderer.Write(block);
            writer.Flush();
            if (builder.Length > 0)
            {
                blocks.Add(new MarkdownBlock(builder.ToString(), block.Line));
            }
        }

        return new MarkdownAnalysis(blocks, headings, painter?.ToRuns() ?? [], frontMatter);
    }

    /// <summary>
    /// Only ordinary web, mail and relative links are kept. Anything that could run code when clicked
    /// (javascript:, vbscript:, data: documents…) is rejected.
    /// </summary>
    public static bool IsSafeUrl(string? url, bool isImage)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var trimmed = url.AsSpan().Trim();
        var colon = trimmed.IndexOf(':');
        var firstDelimiter = trimmed.IndexOfAny('/', '?', '#');
        if (colon < 0 || (firstDelimiter >= 0 && firstDelimiter < colon))
        {
            return true; // relative path, query or fragment
        }

        var scheme = trimmed[..colon];
        if (scheme.Length == 1 && char.IsAsciiLetter(scheme[0]))
        {
            return true; // Windows drive letter
        }

        if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (isImage)
        {
            return trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
                   !trimmed.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase);
        }

        return scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase) || scheme.Equals("tel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelativeUrl(string url)
    {
        if (url.Length == 0 || url[0] == '#')
        {
            return false;
        }

        var colon = url.IndexOf(':');
        var firstDelimiter = url.AsSpan().IndexOfAny('/', '?', '#');
        return (colon < 0 || (firstDelimiter >= 0 && firstDelimiter < colon)) && !url.StartsWith("//", StringComparison.Ordinal);
    }

    private static void Visit(MarkdownObject node, SyntaxPainter? painter, List<HeadingInfo> headings, string text)
    {
        switch (node)
        {
            case HeadingBlock heading:
                painter?.Paint(heading.Span, SyntaxColor.Heading, SyntaxFlags.Bold);
                headings.Add(new HeadingInfo(heading.Level, GetPlainText(heading.Inline), heading.Line, heading.Span.Start, heading.TryGetAttributes()?.Id));
                break;
            case QuoteBlock quote:
                painter?.Paint(quote.Span, SyntaxColor.Quote, SyntaxFlags.None);
                break;
            case YamlFrontMatterBlock frontMatterBlock:
                painter?.Paint(frontMatterBlock.Span, SyntaxColor.Meta, SyntaxFlags.None);
                break;
            case CodeBlock code:
                painter?.Paint(code.Span, SyntaxColor.Code, SyntaxFlags.None);
                break;
            case ThematicBreakBlock rule:
                painter?.Paint(rule.Span, SyntaxColor.Marker, SyntaxFlags.None);
                break;
            case ListItemBlock item when painter is not null:
                // Colour just the bullet or number, which ends at the first whitespace.
                var end = item.Span.Start;
                while (end < text.Length && end <= item.Span.End && !char.IsWhiteSpace(text[end]))
                {
                    end++;
                }

                painter.Paint(new SourceSpan(item.Span.Start, end - 1), SyntaxColor.Marker, SyntaxFlags.None);
                break;
            case TaskList task:
                painter?.Paint(task.Span, SyntaxColor.Marker, SyntaxFlags.None);
                break;
            case EmphasisInline emphasis:
                painter?.Paint(emphasis.Span, SyntaxColor.None, emphasis.DelimiterChar == '~'
                    ? SyntaxFlags.Strike
                    : emphasis.DelimiterCount >= 2 ? SyntaxFlags.Bold : SyntaxFlags.Italic);
                break;
            case CodeInline codeInline:
                painter?.Paint(codeInline.Span, SyntaxColor.Code, SyntaxFlags.None);
                break;
            case LinkInline link:
                if (!IsSafeUrl(link.Url, link.IsImage))
                {
                    link.Url = link.IsImage ? "" : "#";
                }

                painter?.Paint(link.Span, SyntaxColor.Link, SyntaxFlags.None);
                break;
            case AutolinkInline autolink:
                painter?.Paint(autolink.Span, SyntaxColor.Link, SyntaxFlags.None);
                if (!autolink.IsEmail && !IsSafeUrl(autolink.Url, isImage: false))
                {
                    autolink.ReplaceBy(new LiteralInline(autolink.Url));
                    return;
                }

                break;
        }

        switch (node)
        {
            case ContainerBlock container:
                foreach (var child in container)
                {
                    Visit(child, painter, headings, text);
                }

                break;
            case LeafBlock { Inline: { } inline }:
                Visit(inline, painter, headings, text);
                break;
            case ContainerInline containerInline:
                // Copy first: an unsafe autolink replaces itself while we iterate.
                foreach (var child in containerInline.ToList())
                {
                    Visit(child, painter, headings, text);
                }

                break;
        }
    }

    private static string GetPlainText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var node in inline.Descendants())
        {
            switch (node)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Renders fenced code with a known language through ColorCode; everything else as Markdig does.</summary>
    private sealed class HighlightingCodeBlockRenderer(CodeBlockRenderer fallback) : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            if (obj is FencedCodeBlock { Info: { Length: > 0 } info } && FindLanguage(info) is { } language)
            {
                var html = new HtmlClassFormatter().GetHtmlString(obj.Lines.ToString(), language);
                renderer.EnsureLine();
                renderer.Write("<div class=\"code\">").Write(html).Write("</div>");
                renderer.EnsureLine();
                return;
            }

            fallback.Write(renderer, obj);
        }

        private static ILanguage? FindLanguage(string info)
        {
            var id = info.Trim().ToLowerInvariant() switch
            {
                "cs" or "csharp" => "c#",
                "js" or "jsx" or "node" => "javascript",
                "ts" or "tsx" => "typescript",
                "py" => "python",
                "ps1" or "pwsh" => "powershell",
                "c++" or "cc" or "h" or "hpp" or "c" => "cpp",
                "htm" => "html",
                "xaml" or "svg" => "xml",
                "fs" => "f#",
                "vb" => "vb.net",
                var other => other,
            };

            try
            {
                return Languages.FindById(id);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
