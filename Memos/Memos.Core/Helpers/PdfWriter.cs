using System.Globalization;
using System.Text;

namespace Memos.Core.Helpers;

/// <summary>A page of a PDF made of one JPEG picture that covers it.</summary>
public sealed record PdfPage(byte[] Jpeg, int PixelWidth, int PixelHeight);

/// <summary>
/// Writes the smallest valid PDF that shows one picture per page. Pages are pictures of the
/// laid-out document, so the PDF looks exactly like the print, without a PDF library.
/// </summary>
public static class PdfWriter
{
    /// <param name="width">Page width in points (1/72 inch).</param>
    /// <param name="height">Page height in points.</param>
    public static byte[] Write(IReadOnlyList<PdfPage> pages, double width, double height)
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();

        void Text(string text) => output.Write(Encoding.Latin1.GetBytes(text));

        void Begin(int id)
        {
            offsets.Add(output.Position);
            Text(Invariant($"{id} 0 obj\n"));
        }

        // Objects: 1 catalog, 2 page tree, then for each page: the page, its picture and its contents.
        Text("%PDF-1.4\n%âãÏÓ\n");
        Begin(1);
        Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin(2);
        var kids = string.Join(" ", pages.Select((_, i) => Invariant($"{3 + i * 3} 0 R")));
        Text(Invariant($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n"));

        var w = width.ToString("0.##", CultureInfo.InvariantCulture);
        var h = height.ToString("0.##", CultureInfo.InvariantCulture);
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var id = 3 + i * 3;
            Begin(id);
            Text(Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 {id + 1} 0 R >> >> /Contents {id + 2} 0 R >>\nendobj\n"));

            Begin(id + 1);
            Text(Invariant($"<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n"));
            output.Write(page.Jpeg);
            Text("\nendstream\nendobj\n");

            var contents = Invariant($"q {w} 0 0 {h} 0 0 cm /Im0 Do Q");
            Begin(id + 2);
            Text(Invariant($"<< /Length {contents.Length} >>\nstream\n{contents}\nendstream\nendobj\n"));
        }

        var xref = output.Position;
        Text(Invariant($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f\r\n"));
        foreach (var offset in offsets)
        {
            Text(Invariant($"{offset:D10} 00000 n\r\n"));
        }

        Text(Invariant($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return output.ToArray();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
