using Markdig.Syntax;
using Memos.Core.Helpers;

namespace Memos.Core.Markdown;

public enum SyntaxColor : byte
{
    None,
    Heading,
    Code,
    Link,
    Quote,
    Marker,
    Meta,
}

[Flags]
public enum SyntaxFlags : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
}

/// <summary>A stretch of source text that the editor draws with one style. Runs never overlap.</summary>
public readonly record struct SyntaxRun(int Start, int Length, SyntaxColor Color, SyntaxFlags Flags)
{
    public int End => Start + Length;
}

/// <summary>
/// Collects styles per character while the syntax tree is walked parent-first, so nested elements
/// combine (bold inside a heading) and the result can be flattened into non-overlapping runs.
/// </summary>
internal sealed class SyntaxPainter(int length)
{
    private const int FlagBits = 3;
    private const byte FlagMask = (1 << FlagBits) - 1;
    private readonly byte[] _styles = new byte[length];

    public void Paint(SourceSpan span, SyntaxColor color, SyntaxFlags flags)
    {
        var start = Math.Max(0, span.Start);
        var end = Math.Min(_styles.Length - 1, span.End);
        for (var i = start; i <= end; i++)
        {
            var current = _styles[i];
            var colorBits = color == SyntaxColor.None ? current & ~FlagMask : (byte)color << FlagBits;
            _styles[i] = (byte)(colorBits | (current & FlagMask) | (byte)flags);
        }
    }

    public List<SyntaxRun> ToRuns()
    {
        var runs = new List<SyntaxRun>();
        ReadOnlySpan<byte> styles = _styles;
        var i = 0;
        while (i < styles.Length)
        {
            var style = styles[i];
            var start = i;
            var same = styles[i..].IndexOfAnyExcept(style);
            i = same < 0 ? styles.Length : i + same;

            if (style != 0)
            {
                runs.Add(new SyntaxRun(start, i - start, (SyntaxColor)(style >> FlagBits), (SyntaxFlags)(style & FlagMask)));
            }
        }

        return runs;
    }
}

public static class HighlightPlanner
{
    /// <summary>
    /// Given the runs before and after an edit, returns the range of the new text whose styling has
    /// to be redone. Runs that did not change (allowing for the shift the edit caused) are left alone,
    /// because the editing control already carries their formatting.
    /// </summary>
    public static (int Start, int End) GetDirtyRange(
        IReadOnlyList<SyntaxRun> oldRuns, IReadOnlyList<SyntaxRun> newRuns, TextChange change, int newTextLength)
    {
        var changeEndOld = change.Start + change.RemovedLength;
        var changeEndNew = change.Start + change.InsertedLength;
        var delta = change.Delta;
        var common = Math.Min(oldRuns.Count, newRuns.Count);

        var prefix = 0;
        while (prefix < common && oldRuns[prefix] == newRuns[prefix] && oldRuns[prefix].End <= change.Start)
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < common - prefix)
        {
            var o = oldRuns[oldRuns.Count - 1 - suffix];
            var n = newRuns[newRuns.Count - 1 - suffix];
            if (o.Start < changeEndOld || o with { Start = o.Start + delta } != n)
            {
                break;
            }

            suffix++;
        }

        var start = change.Start;
        var end = changeEndNew;
        if (prefix < newRuns.Count - suffix)
        {
            start = Math.Min(start, newRuns[prefix].Start);
            end = Math.Max(end, newRuns[newRuns.Count - suffix - 1].End);
        }

        if (prefix < oldRuns.Count - suffix)
        {
            start = Math.Min(start, oldRuns[prefix].Start);
            end = Math.Max(end, oldRuns[oldRuns.Count - suffix - 1].End + delta);
        }

        start = Math.Clamp(start, 0, newTextLength);
        return (start, Math.Clamp(end, start, newTextLength));
    }
}
