using Memos.Core.Helpers;
using Memos.Core.Markdown;
using Memos.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;

namespace Memos.Services;

/// <summary>
/// Exports to PDF the same pages the printer gets: laid out as native text on A4 sheets, which are
/// then pictured one by one. The text of the PDF is therefore not selectable.
/// </summary>
public sealed class NativePdfExporter(WindowContext context, ISettingsService settings) : IPdfExporter
{
    /// <summary>A4 in the 1/96 inch units of XAML.</summary>
    private static readonly Size PageSize = new(793.7, 1122.5);

    /// <summary>Pixels per XAML unit of the pictures: 192 dpi, sharp enough for text on paper.</summary>
    private const double Scale = 2;

    public async Task ExportAsync(IReadOnlyList<PrintBlock> blocks, string? baseDirectory, string outputPath)
    {
        var host = context.HiddenHost ?? throw new InvalidOperationException("The window is not ready.");
        if (File.Exists(outputPath) && File.GetAttributes(outputPath).HasFlag(FileAttributes.ReadOnly))
        {
            throw new ReadOnlyFileException(outputPath);
        }

        var images = new Dictionary<string, BitmapImage?>(StringComparer.Ordinal);
        await MarkdownImages.LoadAsync(blocks, baseDirectory, settings.Current.AllowRemoteImages, images);

        var page = new PrintPageDescription { PageSize = PageSize, ImageableRect = new Rect(default, PageSize), DpiX = 96, DpiY = 96 };
        var pages = new Paginator(host, page);
        var (family, size) = WindowsPrintService.GetMarkdownFont(settings.Current);
        var opacity = host.Opacity;
        try
        {
            new MarkdownLayout(pages, images, family, size, MarkdownPalette.Light).Create(blocks);

            // A hidden panel renders nothing, so the sheets are shown, but off the window.
            foreach (var sheet in pages.Pages)
            {
                Canvas.SetLeft(sheet, -PageSize.Width * 4);
            }

            host.Opacity = 1;
            var pictures = new List<PdfPage>(pages.Pages.Count);
            foreach (var sheet in pages.Pages)
            {
                pictures.Add(await PictureAsync(sheet));
            }

            await File.WriteAllBytesAsync(outputPath, PdfWriter.Write(pictures, PageSize.Width * 72 / 96, PageSize.Height * 72 / 96));
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            host.Opacity = opacity;
            foreach (var sheet in pages.Pages)
            {
                host.Children.Remove(sheet);
            }
        }
    }

    private static async Task<PdfPage> PictureAsync(FrameworkElement sheet)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(sheet, (int)(PageSize.Width * Scale), (int)(PageSize.Height * Scale));
        var pixels = await bitmap.GetPixelsAsync();

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream,
            [new("ImageQuality", new BitmapTypedValue(0.9f, PropertyType.Single))]);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * Scale, 96 * Scale, pixels.ToArray());
        await encoder.FlushAsync();

        var jpeg = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(jpeg.AsBuffer(), (uint)jpeg.Length, InputStreamOptions.None);
        return new PdfPage(jpeg, bitmap.PixelWidth, bitmap.PixelHeight);
    }
}
