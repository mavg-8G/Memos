using Memos.Core.Markdown;
using Memos.Core.Models;

namespace Memos.Tests;

public sealed class PrintTests
{
    [Fact]
    public async Task Printing_plain_text_sends_the_text_itself_in_the_editor_font()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([app.WriteFile("código.txt", "if (a < b && c > d) {\n\t\"x\"\n}")]);

        await app.Main.PrintCommand.ExecuteAsync(null);

        Assert.Equal("código.txt", app.Print.Title);
        Assert.Equal("if (a < b && c > d) {\n\t\"x\"\n}", app.Print.Text);
        Assert.Null(app.Print.Blocks);
        Assert.Empty(app.Dialogs.Messages);
    }

    [Fact]
    public async Task Printing_markdown_sends_its_blocks_and_not_its_syntax()
    {
        using var app = new TestApp();
        var path = app.WriteFile("doc.md", "# Título\n\ntexto **fuerte**");
        await app.Main.InitializeAsync([path]);

        await app.Main.PrintCommand.ExecuteAsync(null);

        Assert.Equal("doc.md", app.Print.Title);
        Assert.Equal(Path.GetDirectoryName(path), app.Print.BaseDirectory);
        Assert.Equal([PrintBlockKind.Heading, PrintBlockKind.Paragraph], app.Print.Blocks!.Select(b => b.Kind));
        Assert.Equal([new PrintSpan("Título")], app.Print.Blocks![0].Spans);
        Assert.Equal([new PrintSpan("texto "), new PrintSpan("fuerte", Bold: true)], app.Print.Blocks![1].Spans);
        Assert.Null(app.Print.Text);
    }

    [Fact]
    public async Task Printing_uses_what_is_on_screen_not_what_is_on_disk()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([app.WriteFile("nota.txt", "viejo")]);
        app.Active.Select(0, app.Active.Text.Length);
        app.Type("nuevo");

        await app.Main.PrintCommand.ExecuteAsync(null);

        Assert.Equal("nuevo", app.Print.Text);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    [InlineData(typeof(TimeoutException))]
    public async Task A_failed_print_is_reported_and_does_not_escape(Type error)
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Type("x");
        app.Print.Error = (Exception)Activator.CreateInstance(error, "sin impresora")!;

        await app.Main.PrintCommand.ExecuteAsync(null);

        Assert.StartsWith("Msg_PrintError(", Assert.Single(app.Dialogs.Messages));
        Assert.Contains("sin impresora", app.Dialogs.Messages[0]);
    }

    [Fact]
    public async Task Print_is_available_for_every_kind_of_document()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        Assert.True(app.Main.PrintCommand.CanExecute(null));
        app.Main.NewMarkdownCommand.Execute(null);
        Assert.True(app.Main.PrintCommand.CanExecute(null));
    }
}
