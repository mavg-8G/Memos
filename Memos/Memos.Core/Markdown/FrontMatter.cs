using System.Text;

namespace Memos.Core.Markdown;

/// <summary>
/// YAML front matter: the metadata block between <c>---</c> lines at the very top of a Markdown
/// file. It is kept apart from the document body, never interpreted as Markdown.
/// </summary>
public sealed record FrontMatter(string Raw, IReadOnlyList<KeyValuePair<string, string>> Entries)
{
    /// <summary>Value of a top-level key (case-insensitive), or null.</summary>
    public string? this[string key] =>
        Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } entry
            ? entry.Value
            : null;

    /// <summary>
    /// Reads the top-level <c>key: value</c> pairs. Nested or multi-line values are kept as the raw
    /// text under their key; this is for display, not a full YAML parser. Entries are empty when
    /// the block is not a simple mapping.
    /// </summary>
    public static FrontMatter Parse(string raw)
    {
        var entries = new List<KeyValuePair<string, string>>();
        string? key = null;
        var value = new StringBuilder();

        void Flush()
        {
            if (key is not null)
            {
                entries.Add(new(key, Unquote(value.ToString().Trim())));
            }

            key = null;
            value.Clear();
        }

        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0 || trimmed.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var isContinuation = char.IsWhiteSpace(line[0]) || trimmed.StartsWith('-');
            if (isContinuation)
            {
                if (key is null)
                {
                    return new FrontMatter(raw, []);
                }

                value.Append(value.Length > 0 ? "\n" : "").Append(trimmed.Trim());
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0 || (colon + 1 < trimmed.Length && trimmed[colon + 1] != ' '))
            {
                return new FrontMatter(raw, []);
            }

            Flush();
            key = trimmed[..colon].Trim();
            value.Append(trimmed[(colon + 1)..].Trim());
        }

        Flush();
        return new FrontMatter(raw, entries);
    }

    /// <summary>A table of the entries, or the raw text when the block is not a simple mapping.</summary>
    public string ToHtml()
    {
        var sb = new StringBuilder();
        if (Entries.Count == 0)
        {
            return sb.Append("<pre class=\"front-matter\">").Append(Escape(Raw.Trim('\n'))).Append("</pre>\n").ToString();
        }

        sb.Append("<table class=\"front-matter\"><tbody>\n");
        foreach (var (key, value) in Entries)
        {
            sb.Append("<tr><th>").Append(Escape(key)).Append("</th><td>").Append(Escape(value).Replace("\n", "<br />")).Append("</td></tr>\n");
        }

        return sb.Append("</tbody></table>\n").ToString();
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'' ? value[1..^1] : value;

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
