using System.Globalization;

namespace Memos.Core.Helpers;

/// <summary>Offsets of every line start in a '\n'-separated text. Lines are zero-based here.</summary>
public sealed class LineIndex
{
    private readonly int[] _starts;
    private readonly int _textLength;

    public LineIndex(string text)
    {
        var starts = new List<int>(Math.Max(16, text.Length / 32)) { 0 };
        var span = text.AsSpan();
        var offset = 0;
        while (true)
        {
            var i = span[offset..].IndexOf('\n');
            if (i < 0)
            {
                break;
            }

            offset += i + 1;
            starts.Add(offset);
        }

        _starts = [.. starts];
        _textLength = text.Length;
    }

    public int LineCount => _starts.Length;

    public int GetLineFromOffset(int offset)
    {
        offset = Math.Clamp(offset, 0, _textLength);
        var i = Array.BinarySearch(_starts, offset);
        return i >= 0 ? i : ~i - 1;
    }

    public int GetLineStart(int line) => _starts[Math.Clamp(line, 0, _starts.Length - 1)];

    /// <summary>End of the line, not counting its '\n'.</summary>
    public int GetLineEnd(int line)
    {
        line = Math.Clamp(line, 0, _starts.Length - 1);
        return line + 1 < _starts.Length ? _starts[line + 1] - 1 : _textLength;
    }

    /// <summary>
    /// One-based column as the user perceives it: grapheme clusters between the line start and the offset.
    /// </summary>
    public static int GetColumn(string text, int lineStart, int offset)
    {
        offset = Math.Clamp(offset, lineStart, text.Length);
        var span = text.AsSpan(lineStart, offset - lineStart);
        if (System.Text.Ascii.IsValid(span))
        {
            return span.Length + 1;
        }

        var column = 1;
        while (!span.IsEmpty)
        {
            span = span[StringInfo.GetNextTextElementLength(span)..];
            column++;
        }

        return column;
    }
}
