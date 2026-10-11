using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Memos.Core.Markdown;

/// <summary>
/// The smallest change that turns the blocks shown in the preview into new ones: the middle part
/// whose HTML differs is replaced, and blocks that merely moved to another source line (everything
/// below a line that was added or removed) only get their line number rewritten.
/// </summary>
/// <param name="Start">Index of the first block to replace.</param>
/// <param name="RemoveCount">How many shown blocks are removed at <paramref name="Start"/>.</param>
/// <param name="Inserted">Blocks inserted at <paramref name="Start"/>.</param>
/// <param name="LinesStart">Index, after the replacement, of the first block whose line is rewritten.</param>
/// <param name="Lines">Source lines of the blocks from <paramref name="LinesStart"/> on; empty when no kept block moved.</param>
public sealed record PreviewPatch(int Start, int RemoveCount, IReadOnlyList<MarkdownBlock> Inserted, int LinesStart, IReadOnlyList<int> Lines)
{
    /// <summary>Returns null when both lists show the same thing.</summary>
    public static PreviewPatch? Create(IReadOnlyList<MarkdownBlock> oldBlocks, IReadOnlyList<MarkdownBlock> newBlocks)
    {
        var common = Math.Min(oldBlocks.Count, newBlocks.Count);
        var prefix = 0;
        while (prefix < common && SameHtml(oldBlocks[prefix], newBlocks[prefix]))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < common - prefix && SameHtml(oldBlocks[oldBlocks.Count - 1 - suffix], newBlocks[newBlocks.Count - 1 - suffix]))
        {
            suffix++;
        }

        var removeCount = oldBlocks.Count - prefix - suffix;
        var insertCount = newBlocks.Count - prefix - suffix;

        // First kept block that is now on another line; the ones inserted in between are skipped
        // because they are written with their line already.
        var linesStart = -1;
        for (var i = 0; i < prefix && linesStart < 0; i++)
        {
            if (oldBlocks[i].StartLine != newBlocks[i].StartLine)
            {
                linesStart = i;
            }
        }

        for (var i = newBlocks.Count - suffix; i < newBlocks.Count && linesStart < 0; i++)
        {
            if (oldBlocks[i - insertCount + removeCount].StartLine != newBlocks[i].StartLine)
            {
                linesStart = i;
            }
        }

        if (removeCount == 0 && insertCount == 0 && linesStart < 0)
        {
            return null;
        }

        var inserted = new MarkdownBlock[insertCount];
        for (var i = 0; i < insertCount; i++)
        {
            inserted[i] = newBlocks[prefix + i];
        }

        int[] lines = [];
        if (linesStart >= 0)
        {
            lines = new int[newBlocks.Count - linesStart];
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = newBlocks[linesStart + i].StartLine;
            }
        }

        return new PreviewPatch(prefix, removeCount, inserted, Math.Max(linesStart, 0), lines);
    }

    /// <summary>Script that applies the patch to the children of the page's content element.</summary>
    public string ToScript()
    {
        var html = new StringBuilder();
        foreach (var block in Inserted)
        {
            html.Append(HtmlDocumentBuilder.WrapBlock(block));
        }

        var script = new StringBuilder(html.Length + Lines.Count * 6 + 512);
        script.Append("(function(s,r,h,l,n){var c=document.getElementById('").Append(HtmlDocumentBuilder.ContentElementId).Append("'),e=c.children[s]||null,x,i;")
              .Append("for(i=0;i<r&&e;i++){x=e.nextElementSibling;c.removeChild(e);e=x;}")
              .Append("if(h){x=document.createElement('template');x.innerHTML=h;c.insertBefore(x.content,e);}")
              .Append("for(i=0,e=n.length?c.children[l]:null;i<n.length&&e;i++,e=e.nextElementSibling){e.dataset.line=n[i];}})(")
              .Append(Start.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(RemoveCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(JsonSerializer.Serialize(html.ToString())).Append(',')
              .Append(LinesStart.ToString(CultureInfo.InvariantCulture)).Append(",[");
        for (var i = 0; i < Lines.Count; i++)
        {
            if (i > 0)
            {
                script.Append(',');
            }

            script.Append(Lines[i].ToString(CultureInfo.InvariantCulture));
        }

        return script.Append("]);").ToString();
    }

    // Identical blocks of consecutive analyses are usually different string instances, so the
    // reference check only helps when the same analysis is shown again.
    private static bool SameHtml(MarkdownBlock a, MarkdownBlock b) =>
        ReferenceEquals(a.Html, b.Html) || string.Equals(a.Html, b.Html, StringComparison.Ordinal);
}
