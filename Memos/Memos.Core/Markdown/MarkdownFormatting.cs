using System.Text;
using System.Text.RegularExpressions;
using Memos.Core.Helpers;
using Memos.Core.Models;

namespace Memos.Core.Markdown;

/// <summary>
/// The toolbar's formatting actions. They only ever write standard Markdown into the source, so
/// documents stay portable to any other editor.
/// </summary>
public static partial class MarkdownFormatting
{
    [GeneratedRegex(@"^(\s*)#{1,6}[ \t]+")]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^(\s*)(?:[-*+][ \t]+\[[ xX]\][ \t]+|[-*+][ \t]+|\d+[.)][ \t]+|>[ \t]?)")]
    private static partial Regex BlockPrefix();

    /// <summary>Wraps the selection in a marker such as ** or removes it if it is already there.</summary>
    public static TextEdit ToggleInline(string text, int selectionStart, int selectionLength, string marker, string placeholder)
    {
        var end = selectionStart + selectionLength;
        var m = marker.Length;

        // Markers just outside the selection: **|text|**
        if (selectionStart >= m && end + m <= text.Length &&
            text.AsSpan(selectionStart - m, m).SequenceEqual(marker) && text.AsSpan(end, m).SequenceEqual(marker))
        {
            return new TextEdit(selectionStart - m, selectionLength + 2 * m, text.Substring(selectionStart, selectionLength), selectionStart - m, selectionLength);
        }

        var selected = text.Substring(selectionStart, selectionLength);

        // Markers inside the selection: |**text**|
        if (selected.Length >= 2 * m + 1 && selected.StartsWith(marker, StringComparison.Ordinal) && selected.EndsWith(marker, StringComparison.Ordinal))
        {
            var inner = selected[m..^m];
            return new TextEdit(selectionStart, selectionLength, inner, selectionStart, inner.Length);
        }

        if (selected.Length == 0)
        {
            return new TextEdit(selectionStart, 0, marker + placeholder + marker, selectionStart + m, placeholder.Length);
        }

        // Keep surrounding spaces outside the markers; "** text **" is not emphasis in CommonMark.
        var lead = selected.Length - selected.TrimStart().Length;
        var trail = selected.Length - selected.TrimEnd().Length;
        if (lead + trail >= selected.Length)
        {
            return new TextEdit(selectionStart, selectionLength, marker + placeholder + marker, selectionStart + m, placeholder.Length);
        }

        var core = selected[lead..^trail];
        var wrapped = selected[..lead] + marker + core + marker + selected[(selected.Length - trail)..];
        return new TextEdit(selectionStart, selectionLength, wrapped, selectionStart + lead + m, core.Length);
    }

    /// <summary>Turns the selected lines into headings of the given level; level 0 removes the heading.</summary>
    public static TextEdit? SetHeading(string text, int selectionStart, int selectionLength, int level)
    {
        var prefix = level <= 0 ? "" : new string('#', Math.Min(level, 6)) + " ";
        return TransformBlock(text, selectionStart, selectionLength, (line, _) =>
        {
            if (line.Trim().Length == 0 && selectionLength > 0)
            {
                return line;
            }

            var stripped = HeadingPrefix().Replace(line, "$1");
            return prefix + stripped;
        });
    }

    public static TextEdit? ToggleQuote(string text, int selectionStart, int selectionLength) =>
        ToggleLinePrefix(text, selectionStart, selectionLength, _ => "> ", line => line.TrimStart().StartsWith('>'));

    public static TextEdit? ToggleBulletList(string text, int selectionStart, int selectionLength) =>
        ToggleLinePrefix(text, selectionStart, selectionLength, _ => "- ",
            line => Regex.IsMatch(line, @"^\s*[-*+][ \t]+(?!\[[ xX]\])"));

    public static TextEdit? ToggleNumberedList(string text, int selectionStart, int selectionLength) =>
        ToggleLinePrefix(text, selectionStart, selectionLength, index => $"{index + 1}. ",
            line => Regex.IsMatch(line, @"^\s*\d+[.)][ \t]+"));

    public static TextEdit? ToggleTaskList(string text, int selectionStart, int selectionLength) =>
        ToggleLinePrefix(text, selectionStart, selectionLength, _ => "- [ ] ",
            line => Regex.IsMatch(line, @"^\s*[-*+][ \t]+\[[ xX]\]"));

    public static TextEdit InsertLink(string text, int selectionStart, int selectionLength, string label, string url)
    {
        var link = $"[{EscapeLabel(label)}]({FormatDestination(url)})";
        return new TextEdit(selectionStart, selectionLength, link, selectionStart + link.Length, 0);
    }

    public static TextEdit InsertImage(string text, int selectionStart, int selectionLength, string altText, string url)
    {
        var image = $"![{EscapeLabel(altText)}]({FormatDestination(url)})";
        return new TextEdit(selectionStart, selectionLength, image, selectionStart + image.Length, 0);
    }

    /// <summary>Fences the selected lines as a code block, or inserts an empty one with the caret inside.</summary>
    public static TextEdit InsertCodeBlock(string text, int selectionStart, int selectionLength)
    {
        if (selectionLength == 0)
        {
            var (before, after) = GetBlockSeparators(text, selectionStart, selectionStart);
            var insert = before + "```\n\n```" + after;
            return new TextEdit(selectionStart, 0, insert, selectionStart + before.Length + 4, 0);
        }

        var (start, end) = TextOperations.GetLineBlock(text, selectionStart, selectionLength);
        var body = text[start..end];
        var fence = body.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        var (lead, tail) = GetBlockSeparators(text, start, end);
        var block = $"{lead}{fence}\n{body}\n{fence}{tail}";
        return new TextEdit(start, end - start, block, start + lead.Length + fence.Length + 1, body.Length);
    }

    public static TextEdit InsertHorizontalRule(string text, int selectionStart, int selectionLength)
    {
        var end = selectionStart + selectionLength;
        var (before, after) = GetBlockSeparators(text, selectionStart, end);
        var insert = before + "---" + (after.Length == 0 ? "\n" : after);
        return new TextEdit(selectionStart, selectionLength, insert, selectionStart + insert.Length, 0);
    }

    public static TextEdit InsertTable(string text, int selectionStart, int selectionLength, int rows, int columns, string headerLabel)
    {
        rows = Math.Clamp(rows, 1, 50);
        columns = Math.Clamp(columns, 1, 20);

        var sb = new StringBuilder();
        var end = selectionStart + selectionLength;
        var (before, after) = GetBlockSeparators(text, selectionStart, end);
        sb.Append(before);
        var firstCell = sb.Length + 2;

        sb.Append('|');
        for (var c = 1; c <= columns; c++)
        {
            sb.Append(' ').Append(headerLabel).Append(' ').Append(c).Append(" |");
        }

        sb.Append("\n|");
        for (var c = 0; c < columns; c++)
        {
            sb.Append(" --- |");
        }

        for (var r = 0; r < rows; r++)
        {
            sb.Append("\n|");
            for (var c = 0; c < columns; c++)
            {
                sb.Append("     |");
            }
        }

        sb.Append(after.Length == 0 ? "\n" : after);
        var firstHeader = $"{headerLabel} 1";
        return new TextEdit(selectionStart, selectionLength, sb.ToString(), selectionStart + firstCell, firstHeader.Length);
    }

    /// <summary>Blank lines needed before and after a block inserted between two offsets.</summary>
    private static (string Before, string After) GetBlockSeparators(string text, int start, int end)
    {
        var before = "";
        if (start > 0)
        {
            before = text[start - 1] != '\n' ? "\n\n" : start > 1 && text[start - 2] != '\n' ? "\n" : "";
        }

        var after = "";
        if (end < text.Length)
        {
            after = text[end] != '\n' ? "\n\n" : end + 1 < text.Length && text[end + 1] != '\n' ? "\n" : "";
        }

        return (before, after);
    }

    private static TextEdit? ToggleLinePrefix(string text, int selectionStart, int selectionLength, Func<int, string> prefix, Func<string, bool> hasPrefix)
    {
        var (start, end) = TextOperations.GetLineBlock(text, selectionStart, selectionLength);
        var lines = text[start..end].Split('\n');
        var relevant = lines.Where(l => l.Trim().Length > 0 || lines.Length == 1).ToList();
        var remove = relevant.Count > 0 && relevant.All(hasPrefix);

        var index = 0;
        return TransformBlock(text, selectionStart, selectionLength, (line, isOnlyLine) =>
        {
            if (line.Trim().Length == 0 && !isOnlyLine)
            {
                return line;
            }

            var match = BlockPrefix().Match(line);
            var indent = match.Success ? match.Groups[1].Value : line[..(line.Length - line.TrimStart().Length)];
            var content = match.Success ? line[match.Length..] : line[indent.Length..];
            return remove ? indent + content : indent + prefix(index++) + content;
        });
    }

    private static TextEdit? TransformBlock(string text, int selectionStart, int selectionLength, Func<string, bool, string> transform)
    {
        var (start, end) = TextOperations.GetLineBlock(text, selectionStart, selectionLength);
        var lines = text[start..end].Split('\n');
        var result = string.Join('\n', lines.Select(line => transform(line, lines.Length == 1)));
        if (result == text[start..end])
        {
            return null;
        }

        // A single line keeps the caret after the same content; several lines stay selected as a block.
        return lines.Length == 1
            ? new TextEdit(start, end - start, result, Math.Max(start, selectionStart + selectionLength + result.Length - (end - start)), 0)
            : new TextEdit(start, end - start, result, start, result.Length);
    }

    private static string EscapeLabel(string label) => label.Replace("[", "\\[").Replace("]", "\\]");

    private static string FormatDestination(string url)
    {
        url = url.Trim().Replace('\\', '/');
        return url.Any(c => char.IsWhiteSpace(c) || c is '(' or ')') ? $"<{url}>" : url;
    }
}
