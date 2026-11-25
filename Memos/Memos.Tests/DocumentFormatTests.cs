using Memos.Core.Models;

namespace Memos.Tests;

public sealed class DocumentFormatTests
{
    [Fact]
    public async Task Markdown_formatting_preserves_the_open_text_file_and_undo_history()
    {
        using var app = new TestApp();
        var path = app.WriteFile("nota.txt", "hola");
        await app.Main.InitializeAsync([path]);
        var document = app.Active;
        document.Select(0, document.Text.Length);

        app.Main.ToggleFormattingCommand.Execute(null);
        Assert.Same(document, app.Active);
        Assert.Equal(path, document.FilePath);
        Assert.False(document.IsModified);
        Assert.Equal(MarkdownViewMode.Split, document.ViewMode);

        app.Main.BoldCommand.Execute(null);
        Assert.Equal("**hola**", document.Text);
        app.Main.ToggleFormattingCommand.Execute(null);
        Assert.Equal(MarkdownViewMode.Editor, document.ViewMode);
        Assert.True(document.CanUndo);
        Assert.True(await app.Main.SaveDocumentAsync(document, saveAs: false));
        Assert.Equal("**hola**", File.ReadAllText(path));

        app.Pickers.SaveAnswer = app.Temp.File("copia.txt");
        Assert.True(await app.Main.SaveDocumentAsync(document, saveAs: true));
        Assert.Equal(DocumentKind.Markdown, document.Kind);
        Assert.Equal("**hola**", File.ReadAllText(app.Pickers.SaveAnswer));
    }

    [Fact]
    public async Task Rtf_paths_cannot_be_opened_or_overwritten()
    {
        using var app = new TestApp();
        const string original = @"{\rtf1\ansi hola\par}";
        var path = app.WriteFile("nota.RTF", original);
        await app.Main.InitializeAsync([path]);
        Assert.Null(app.Active.FilePath);
        Assert.Equal("Msg_RtfUnsupported", Assert.Single(app.Dialogs.Messages));

        app.Type("nuevo");
        app.Pickers.SaveAnswer = path;
        Assert.False(await app.Main.SaveDocumentAsync(app.Active, saveAs: true));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal("nuevo", app.Active.Text);
    }

    [Fact]
    public async Task An_old_session_restores_rtf_drafts_as_unsaved_text_alongside_supported_files()
    {
        using var app = new TestApp();
        var textPath = app.WriteFile("regular.txt", "en disco");
        var rtfPath = app.WriteFile("nota.rtf", @"{\rtf1\ansi original\par}");
        var id = Guid.NewGuid();
        var state = new SessionState
        {
            ActiveIndex = 1,
            Documents =
            [
                new SessionDocument { Id = Guid.NewGuid(), DisplayName = "regular.txt", FilePath = textPath },
                new SessionDocument { Id = id, DisplayName = "nota.rtf", FilePath = rtfPath, HasBackup = true }
            ]
        };
        await app.Session.SaveAsync(state, new Dictionary<Guid, string>
        {
            [id] = @"{\rtf1\ansi a\u241?o \b nuevo\b0\par}"
        });
        var sessionPath = Path.Combine(app.Temp.File("data"), "Session", "session.json");
        var json = File.ReadAllText(sessionPath);
        var legacyEntry = json.LastIndexOf("\"Text\"", StringComparison.Ordinal);
        File.WriteAllText(sessionPath, json.Remove(legacyEntry, "\"Text\"".Length).Insert(legacyEntry, "\"RichText\""));

        app.Settings.RestoreSession = true;
        await app.Main.InitializeAsync([]);

        Assert.Equal(2, app.Main.Documents.Count);
        Assert.Equal(textPath, app.Main.Documents[0].FilePath);
        Assert.Equal(DocumentKind.Text, app.Active.Kind);
        Assert.Null(app.Active.FilePath);
        Assert.True(app.Active.IsModified);
        Assert.Equal("año nuevo", app.Active.Text);
    }
}
