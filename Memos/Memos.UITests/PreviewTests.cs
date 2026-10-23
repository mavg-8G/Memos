using System.Text;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace Memos.UITests;

/// <summary>The Markdown preview and the PDF export, both laid out as native text.</summary>
public class PreviewTests
{
    private const string SplitSettings =
        """{ "Language": "en-US", "MarkdownViewMode": "Split", "WindowWidth": 1200, "WindowHeight": 760 }""";

    private const string Sample =
        "---\ntitle: Report\n---\n# Native preview\n\nSome **bold** text, a [web link](https://example.com), " +
        "a [jump](#far-away) and a [local file](other.md).\n\n```cs\nvar x = \"y\"; // z\n```\n\n| a | b |\n|---|--:|\n| 1 | 2 |\n";

    private static AutomationElement Preview(MemosApp app) => app.FindByName("Preview", ControlType.Pane);

    private static AutomationElement? TryFindIn(AutomationElement scope, string name, ControlType type) =>
        scope.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)));

    /// <summary>Consecutive paragraphs share one text element, so text is looked for by its start.</summary>
    private static AutomationElement? TryFindTextStarting(AutomationElement scope, string start) =>
        scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name.StartsWith(start, StringComparison.Ordinal));

    private static string LongDocument() =>
        Sample + string.Concat(Enumerable.Range(1, 80).Select(i => $"\nParagraph {i} of filler text.\n")) + "\n## Far away\n\nThe end.\n";

    [Fact]
    public void The_preview_shows_the_document_and_follows_typing()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "report.md", Sample)], SplitSettings);

        var preview = Preview(app);
        MemosApp.WaitFor(() => TryFindTextStarting(preview, "Native preview"), "the heading in the preview");
        Assert.NotNull(TryFindIn(preview, "title", ControlType.Text));
        Assert.NotNull(TryFindIn(preview, "Report", ControlType.Text));
        Assert.NotNull(TryFindIn(preview, "web link", ControlType.Hyperlink));
        Assert.NotNull(TryFindIn(preview, "2", ControlType.Text));

        // The caret starts before the front matter: a line typed there turns it into ordinary text.
        app.Type("Draft\n");

        MemosApp.WaitFor(() => TryFindTextStarting(preview, "Draft"), "the edit to reach the preview");
        Assert.Null(TryFindIn(preview, "title", ControlType.Text));
        Assert.False(app.HasExited);
    }

    [Fact]
    public void Links_scroll_to_headings_and_open_local_files_as_tabs()
    {
        using var app = MemosApp.Launch(docs =>
        {
            MemosApp.WriteDoc(docs, "other.md", "# Other\n");
            return [MemosApp.WriteDoc(docs, "long.md", LongDocument())];
        }, SplitSettings);

        var preview = Preview(app);
        var scroll = (ScrollPattern)preview.GetCurrentPattern(ScrollPattern.Pattern);
        var jump = MemosApp.WaitFor(() => TryFindIn(preview, "jump", ControlType.Hyperlink), "the anchor link");
        Assert.Equal(0, scroll.Current.VerticalScrollPercent, 1);

        MemosApp.Invoke(jump);
        MemosApp.Eventually(() => scroll.Current.VerticalScrollPercent > 90, "the preview to scroll to the heading at the end");

        MemosApp.Invoke(TryFindIn(preview, "local file", ControlType.Hyperlink)!);
        MemosApp.Eventually(() => app.TabNames().Contains("other.md"), "the linked file to open as a tab");
    }

    [Fact]
    public void Exporting_to_pdf_writes_pages_with_the_document_on_them()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "long.md", LongDocument())], SplitSettings);
        var output = app.Doc("long.pdf");

        app.Menu("File", "Export", "Export to PDF…");
        SaveAs(app, output);

        MemosApp.Eventually(() => File.Exists(output) && new FileInfo(output).Length > 0, "the PDF to be written", TimeSpan.FromSeconds(30));
        var pdf = File.ReadAllBytes(output);
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Matches(@"/Count [2-9] ", text); // 80 paragraphs do not fit on one A4 page

        // The first page is a picture of the document: not blank, mostly white paper with dark text.
        var start = text.IndexOf("stream\n", text.IndexOf("/DCTDecode", StringComparison.Ordinal), StringComparison.Ordinal) + "stream\n".Length;
        var frame = new JpegBitmapDecoder(new MemoryStream(pdf, start, pdf.Length - start), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Gray8, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight];
        converted.CopyPixels(pixels, converted.PixelWidth, 0);
        var dark = pixels.Count(p => p < 96);
        Assert.InRange(dark, pixels.Length / 1000, pixels.Length / 5);
        Assert.False(app.HasExited);
    }

    /// <summary>Answers the Save dialog of Windows, which the app shows as a window of its own process.</summary>
    private static void SaveAs(MemosApp app, string path)
    {
        var dialog = MemosApp.WaitFor(() => app.OwnWindows().Select(AutomationElement.FromHandle)
            .FirstOrDefault(w => w.Current.ClassName == "#32770"), "the Save dialog");
        var name = MemosApp.WaitFor(() => dialog.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1001"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))), "the file name box");
        ((ValuePattern)name.GetCurrentPattern(ValuePattern.Pattern)).SetValue(path);
        MemosApp.Invoke(dialog.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.AutomationIdProperty, "1")));
    }
}
