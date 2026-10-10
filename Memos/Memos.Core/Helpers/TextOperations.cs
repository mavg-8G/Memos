using System.Globalization;
using System.Text;
using Memos.Core.Models;

namespace Memos.Core.Helpers;

/// <summary>
/// Pure editing operations over '\n'-separated text. Each returns the edit to apply, or null when
/// there is nothing to change, so they are trivially undoable and testable without a UI.
/// </summary>
public static class TextOperations
{
    /// <summary>Start and end (exclusive of the final '\n') of the whole lines touched by a selection.</summary>
    public static (int Start, int End) GetLineBlock(string text, int selectionStart, int selectionLength)
    {
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        var selectionEnd = Math.Clamp(selectionStart + selectionLength, selectionStart, text.Length);

        // A selection that ends right at the start of a line does not include that line.
        if (selectionEnd > selectionStart && text[selectionEnd - 1] == '\n')
        {
            selectionEnd--;
        }

        var start = selectionStart == 0 ? 0 : text.LastIndexOf('\n', selectionStart - 1) + 1;
        var end = text.IndexOf('\n', selectionEnd);
        return (start, end < 0 ? text.Length : end);
    }

    public static TextEdit DuplicateLines(string text, int selectionStart, int selectionLength)
    {
        var (start, end) = GetLineBlock(text, selectionStart, selectionLength);
        var block = text[start..end];
        return new TextEdit(end, 0, "\n" + block, selectionStart + block.Length + 1, selectionLength);
    }

    public static TextEdit? DeleteLines(string text, int selectionStart, int selectionLength)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var (start, end) = GetLineBlock(text, selectionStart, selectionLength);
        if (end < text.Length)
        {
            end++; // take the line's own '\n'
        }
        else if (start > 0)
        {
            start--; // last line: take the preceding '\n' instead
        }

        return end == start ? null : new TextEdit(start, end - start, "", Math.Min(start, text.Length - (end - start)), 0);
    }

    public static TextEdit? MoveLines(string text, int selectionStart, int selectionLength, bool up)
    {
        var (start, end) = GetLineBlock(text, selectionStart, selectionLength);
        var block = text[start..end];

        if (up)
        {
            if (start == 0)
            {
                return null;
            }

            var previousStart = start < 2 ? 0 : text.LastIndexOf('\n', start - 2) + 1;
            var previous = text[previousStart..(start - 1)];
            return new TextEdit(previousStart, end - previousStart, block + "\n" + previous,
                selectionStart - previous.Length - 1, selectionLength);
        }

        if (end >= text.Length)
        {
            return null;
        }

        var nextEnd = text.IndexOf('\n', end + 1);
        if (nextEnd < 0)
        {
            nextEnd = text.Length;
        }

        var next = text[(end + 1)..nextEnd];
        return new TextEdit(start, nextEnd - start, next + "\n" + block,
            selectionStart + next.Length + 1, selectionLength);
    }

    public static TextEdit? ChangeCase(string text, int selectionStart, int selectionLength, bool upper)
    {
        if (selectionLength <= 0)
        {
            return null;
        }

        var selected = text.Substring(selectionStart, selectionLength);
        var culture = CultureInfo.CurrentCulture;
        var changed = upper ? selected.ToUpper(culture) : selected.ToLower(culture);
        return changed == selected
            ? null
            : new TextEdit(selectionStart, selectionLength, changed, selectionStart, changed.Length);
    }

    /// <summary>Removes spaces and tabs at the end of every line (of the selected lines if there is a selection).</summary>
    public static TextEdit? TrimTrailingWhitespace(string text, int selectionStart, int selectionLength)
    {
        var (start, end) = selectionLength > 0 ? GetLineBlock(text, selectionStart, selectionLength) : (0, text.Length);
        var result = TransformLines(text.AsSpan(start, end - start), static (line, sb) => sb.Append(line.TrimEnd(" \t")));
        return BuildBlockEdit(text, start, end, result, selectionStart, selectionLength);
    }

    public static TextEdit Indent(string text, int selectionStart, int selectionLength, string unit)
    {
        var (start, end) = GetLineBlock(text, selectionStart, selectionLength);
        var result = TransformLines(text.AsSpan(start, end - start), (line, sb) =>
        {
            if (!line.IsEmpty)
            {
                sb.Append(unit);
            }

            sb.Append(line);
        });

        var firstLineShift = text.AsSpan(start, Math.Min(end, text.Length) - start).IndexOf('\n') == 0 || end == start ? 0 : unit.Length;
        var newStart = selectionStart + firstLineShift;
        var newEnd = selectionStart + selectionLength + (result.Length - (end - start));
        return new TextEdit(start, end - start, result, newStart, Math.Max(0, newEnd - newStart));
    }

    public static TextEdit? Outdent(string text, int selectionStart, int selectionLength, int tabSize)
    {
        var (start, end) = GetLineBlock(text, selectionStart, selectionLength);
        var firstLineRemoved = -1;
        var result = TransformLines(text.AsSpan(start, end - start), (line, sb) =>
        {
            var remove = 0;
            if (line.StartsWith("\t"))
            {
                remove = 1;
            }
            else
            {
                while (remove < tabSize && remove < line.Length && line[remove] == ' ')
                {
                    remove++;
                }
            }

            if (firstLineRemoved < 0)
            {
                firstLineRemoved = remove;
            }

            sb.Append(line[remove..]);
        });

        var removedTotal = (end - start) - result.Length;
        if (removedTotal == 0)
        {
            return null;
        }

        var newStart = Math.Max(start, selectionStart - Math.Max(0, firstLineRemoved));
        var newEnd = Math.Max(newStart, selectionStart + selectionLength - removedTotal);
        return new TextEdit(start, end - start, result, newStart, newEnd - newStart);
    }

    /// <summary>Rewrites the leading whitespace of every line as spaces or as tabs.</summary>
    public static TextEdit? ConvertIndentation(string text, bool toSpaces, int tabSize)
    {
        tabSize = Math.Max(1, tabSize);
        var result = TransformLines(text, (line, sb) =>
        {
            var i = 0;
            var width = 0;
            while (i < line.Length && line[i] is ' ' or '\t')
            {
                width = line[i] == '\t' ? (width / tabSize + 1) * tabSize : width + 1;
                i++;
            }

            if (toSpaces)
            {
                sb.Append(' ', width);
            }
            else
            {
                sb.Append('\t', width / tabSize).Append(' ', width % tabSize);
            }

            sb.Append(line[i..]);
        });

        return BuildBlockEdit(text, 0, text.Length, result, 0, 0);
    }

    /// <summary>Leading whitespace of the line that contains <paramref name="offset"/>, up to the offset.</summary>
    public static string GetLineIndentation(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var end = start;
        while (end < offset && text[end] is ' ' or '\t')
        {
            end++;
        }

        return text[start..end];
    }

    /// <summary>
    /// Shrinks an edit by dropping the parts of the replacement that equal the text they replace,
    /// so the editing control touches as little as possible.
    /// </summary>
    public static TextEdit Minimize(string text, TextEdit edit)
    {
        if (edit.Length == 0 || edit.NewText.Length == 0)
        {
            return edit;
        }

        var change = TextDiff.Compute(text.Substring(edit.Start, edit.Length), edit.NewText);
        return edit with
        {
            Start = edit.Start + change.Start,
            Length = change.RemovedLength,
            NewText = edit.NewText.Substring(change.Start, change.InsertedLength),
        };
    }

    private delegate void LineTransform(ReadOnlySpan<char> line, StringBuilder output);

    private static string TransformLines(ReadOnlySpan<char> block, LineTransform transform)
    {
        var sb = new StringBuilder(block.Length + 16);
        while (true)
        {
            var i = block.IndexOf('\n');
            if (i < 0)
            {
                transform(block, sb);
                break;
            }

            transform(block[..i], sb);
            sb.Append('\n');
            block = block[(i + 1)..];
        }

        return sb.ToString();
    }

    private static TextEdit? BuildBlockEdit(string text, int start, int end, string result, int selectionStart, int selectionLength)
    {
        if (result.Length == end - start && text.AsSpan(start, end - start).SequenceEqual(result))
        {
            return null;
        }

        // Keep the caret where it was as far as the shorter text allows.
        var newLength = text.Length - (end - start) + result.Length;
        var newStart = Math.Min(selectionStart, newLength);
        return new TextEdit(start, end - start, result, newStart, Math.Min(selectionLength, newLength - newStart));
    }
}
