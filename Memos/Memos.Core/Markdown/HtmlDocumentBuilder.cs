using ColorCode;
using ColorCode.Styling;
using Memos.Core.Models;
using System.Globalization;
using System.Text;

namespace Memos.Core.Markdown;

public sealed record HtmlPageOptions
{
    /// <summary>True or false forces a theme; null follows the reader's system setting.</summary>
    public bool? Dark { get; init; }

    public PreviewStyle Style { get; init; } = PreviewStyle.Default;

    public double FontSize { get; init; } = 15;

    public bool AllowRemoteImages { get; init; }

    /// <summary>Origin that serves the document's folder inside the app preview (null for exported files).</summary>
    public string? LocalResourceOrigin { get; init; }

    /// <summary>The preview lets the window material show through; exported files paint their own background.</summary>
    public bool TransparentBackground { get; init; }

    public string Language { get; init; } = "en";
}

/// <summary>Builds complete, script-free HTML documents for the preview and for export.</summary>
public static class HtmlDocumentBuilder
{
    public const string ContentElementId = "content";

    private static readonly Lazy<string> LightCodeCss = new(() => GetCodeCss(StyleDictionary.DefaultLight));
    private static readonly Lazy<string> DarkCodeCss = new(() => GetCodeCss(StyleDictionary.DefaultDark));

    /// <summary>Each block goes in its own section tagged with its source line, which scroll sync relies on.</summary>
    public static string WrapBlock(MarkdownBlock block) =>
        $"<section data-line=\"{block.StartLine.ToString(CultureInfo.InvariantCulture)}\">{block.Html}</section>";

    public static string WrapBlocks(IEnumerable<MarkdownBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            sb.Append(WrapBlock(block)).Append('\n');
        }

        return sb.ToString();
    }

    public static string BuildPage(string title, string bodyHtml, HtmlPageOptions options)
    {
        // No script source is ever allowed. Images come from the document folder, from data: URIs and,
        // only if the user opted in (or the file is opened outside the app), from the network.
        var imageSources = options.LocalResourceOrigin is { } origin
            ? origin + " data:" + (options.AllowRemoteImages ? " https: http:" : "")
            : "* data: file:";
        var csp = $"default-src 'none'; img-src {imageSources}; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";

        var sb = new StringBuilder(bodyHtml.Length + 8192);
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(Escape(options.Language)).Append("\">\n<head>\n")
          .Append("<meta charset=\"utf-8\">\n")
          .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(csp).Append("\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<title>").Append(Escape(title)).Append("</title>\n<style>\n");

        AppendThemeCss(sb, options);
        sb.Append(BaseCss.Replace("{{font-size}}", options.FontSize.ToString("0.##", CultureInfo.InvariantCulture)));
        sb.Append(options.Style switch
        {
            PreviewStyle.Serif => SerifCss,
            PreviewStyle.Compact => CompactCss,
            _ => "",
        });

        sb.Append("</style>\n</head>\n<body>\n<main id=\"").Append(ContentElementId).Append("\">\n")
          .Append(bodyHtml)
          .Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void AppendThemeCss(StringBuilder sb, HtmlPageOptions options)
    {
        var background = options.TransparentBackground ? "transparent" : "var(--page)";
        switch (options.Dark)
        {
            case true:
                sb.Append(":root{color-scheme:dark;").Append(DarkVariables).Append("}\n").Append(DarkCodeCss.Value);
                break;
            case false:
                sb.Append(":root{color-scheme:light;").Append(LightVariables).Append("}\n").Append(LightCodeCss.Value);
                break;
            default:
                sb.Append(":root{color-scheme:light dark;").Append(LightVariables).Append("}\n").Append(LightCodeCss.Value)
                  .Append("@media (prefers-color-scheme: dark){\n:root{").Append(DarkVariables).Append("}\n")
                  .Append(DarkCodeCss.Value).Append("}\n")
                  .Append("@media print{\n:root{").Append(LightVariables).Append("}\n").Append(LightCodeCss.Value).Append("}\n");
                break;
        }

        sb.Append("html{background:").Append(background).Append("}\n");
    }

    /// <summary>ColorCode's token colours as CSS, without its page-level rules.</summary>
    private static string GetCodeCss(StyleDictionary styles)
    {
        var sb = new StringBuilder();
        foreach (var rule in new HtmlClassFormatter(styles).GetCSSString().Split('}', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Keep only class rules; drop "body{…}" and anything that sets a background.
            var brace = rule.IndexOf('{');
            if (brace <= 0 || rule[0] != '.')
            {
                continue;
            }

            var declarations = rule[(brace + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(d => !d.StartsWith("background", StringComparison.OrdinalIgnoreCase));
            var body = string.Join(';', declarations);
            if (body.Length > 0)
            {
                sb.Append(".code ").Append(rule.AsSpan(0, brace).Trim()).Append('{').Append(body).Append("}\n");
            }
        }

        return sb.ToString();
    }

    private const string LightVariables =
        "--page:#ffffff;--fg:#1f2328;--muted:#59636e;--border:#d1d9e0;--soft:#f6f8fa;--link:#0969da;--mark:#fff8c5";

    private const string DarkVariables =
        "--page:#0d1117;--fg:#f0f6fc;--muted:#9198a1;--border:#3d444d;--soft:#161b22;--link:#4493f8;--mark:#bb800926";

    private const string BaseCss = """
        *{box-sizing:border-box}
        body{margin:0;color:var(--fg);font:{{font-size}}px/1.6 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;overflow-wrap:break-word}
        main{max-width:920px;margin:0 auto;padding:20px 28px 48px}
        section>:first-child{margin-top:0}
        h1,h2,h3,h4,h5,h6{margin:1.4em 0 .6em;line-height:1.25;font-weight:600}
        h1{font-size:2em;padding-bottom:.3em;border-bottom:1px solid var(--border)}
        h2{font-size:1.5em;padding-bottom:.3em;border-bottom:1px solid var(--border)}
        h3{font-size:1.25em} h4{font-size:1em} h5{font-size:.875em} h6{font-size:.85em;color:var(--muted)}
        p,ul,ol,blockquote,table,pre,.code{margin:0 0 1em}
        a{color:var(--link);text-decoration:none} a:hover{text-decoration:underline}
        img{max-width:100%;height:auto}
        hr{height:1px;border:0;background:var(--border);margin:1.5em 0}
        blockquote{padding:0 1em;color:var(--muted);border-left:.25em solid var(--border)}
        blockquote>:last-child{margin-bottom:0}
        ul,ol{padding-left:2em} li+li{margin-top:.25em} li>p{margin-bottom:.5em}
        ul.contains-task-list{list-style:none;padding-left:1.4em}
        .task-list-item input{margin:0 .5em 0 -1.4em;vertical-align:middle}
        code,pre{font-family:"Cascadia Mono","Cascadia Code",Consolas,monospace;font-size:.9em}
        code{padding:.15em .4em;background:var(--soft);border-radius:6px}
        pre{padding:14px 16px;overflow:auto;line-height:1.45;background:var(--soft);border-radius:8px}
        pre code{padding:0;background:none;font-size:1em}
        .code pre{margin:0}
        table{border-collapse:collapse;display:block;width:max-content;max-width:100%;overflow:auto}
        th,td{padding:6px 13px;border:1px solid var(--border)} th{font-weight:600;background:var(--soft)}
        del{color:var(--muted)} mark{background:var(--mark);color:inherit}
        .footnotes{font-size:.875em;color:var(--muted)} .footnotes hr{margin:2em 0 1em}
        sup{line-height:0}
        .front-matter{display:table;width:auto;margin:0 0 1.5em;font-size:.85em;color:var(--muted);border-collapse:collapse}
        .front-matter th,.front-matter td{padding:3px 12px;border:1px solid var(--border);background:none;text-align:left;vertical-align:top}
        .front-matter th{font-weight:600;white-space:nowrap}
        pre.front-matter{display:block;padding:10px 14px;border:1px solid var(--border);background:none}
        @media print{main{max-width:none;padding:0} pre{white-space:pre-wrap} a{color:inherit}}

        """;

    private const string SerifCss = """
        body{font-family:Cambria,Georgia,"Times New Roman",serif;line-height:1.7}
        main{max-width:760px}
        h1,h2{border-bottom:0}

        """;

    private const string CompactCss = """
        body{line-height:1.45}
        main{max-width:none;padding:12px 16px 32px}
        h1,h2,h3,h4,h5,h6{margin:1em 0 .4em}
        p,ul,ol,blockquote,table,pre,.code{margin-bottom:.6em}

        """;
}
