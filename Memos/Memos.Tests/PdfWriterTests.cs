using Memos.Core.Helpers;
using System.Text;

namespace Memos.Tests;

public class PdfWriterTests
{
    [Fact]
    public void Every_page_is_an_object_the_cross_reference_table_points_at()
    {
        byte[] jpeg = [0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9];
        var pdf = Encoding.Latin1.GetString(PdfWriter.Write([new PdfPage(jpeg, 2, 3), new PdfPage(jpeg, 2, 3)], 595.28, 841.89));

        Assert.StartsWith("%PDF-1.4", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
        Assert.Contains("/Count 2", pdf);
        Assert.Contains("/MediaBox [0 0 595.28 841.89]", pdf);

        var xref = int.Parse(pdf.Split("startxref\n")[1].Split('\n')[0]);
        Assert.StartsWith("xref\n0 9\n", pdf[xref..]);
        var entries = pdf[xref..].Split("\r\n").Skip(1).Take(8).ToList();
        for (var id = 1; id <= 8; id++)
        {
            var offset = int.Parse(entries[id - 1][..10]);
            Assert.StartsWith($"{id} 0 obj", pdf[offset..]);
        }
    }
}
