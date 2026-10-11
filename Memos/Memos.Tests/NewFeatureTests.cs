using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;

namespace Memos.Tests;

public class OverwriteModeTests
{
    /// <summary>Simulates the editing control reporting what the user typed at an offset.</summary>
    private static void TypeAt(DocumentViewModel document, int offset, string typed) =>
        document.OnEditorTextChanged(document.Text.Insert(offset, typed));

    private static DocumentViewModel DocumentWith(string text)
    {
        var document = TestHelpers.NewDocument();
        document.ApplyEdit(new TextEdit(0, 0, text, 0, 0));
        return document;
    }

    [Fact]
    public void Typing_replaces_the_character_under_the_caret()
    {
        var document = DocumentWith("hola mundo");
        document.IsOverwrite = true;

        TypeAt(document, 0, "H");
        TypeAt(document, 5, "M");

        Assert.Equal("Hola Mundo", document.Text);
    }

    [Fact]
    public void Insert_mode_is_the_default_and_pushes_text_forward()
    {
        var document = DocumentWith("mundo");

        TypeAt(document, 0, "X");

        Assert.False(document.IsOverwrite);
        Assert.Equal("Xmundo", document.Text);
    }

    [Fact]
    public void Overwriting_never_eats_the_line_break()
    {
        var document = DocumentWith("ab\ncd");
        document.IsOverwrite = true;

        TypeAt(document, 2, "X");
        TypeAt(document, 1, "YZ");

        Assert.Equal("aYZ\ncd", document.Text);
    }

    [Fact]
    public void A_typed_character_replaces_one_whole_grapheme()
    {
        var document = DocumentWith("👨‍👩‍👧‍👦éz");
        document.IsOverwrite = true;

        TypeAt(document, 0, "a");
        Assert.Equal("aéz", document.Text);

        TypeAt(document, 1, "b");
        Assert.Equal("abz", document.Text);
    }

    [Fact]
    public void Each_overwrite_is_undone_together_with_what_it_replaced()
    {
        var document = DocumentWith("hola");
        document.IsOverwrite = true;

        TypeAt(document, 0, "X");
        Assert.Equal("Xola", document.Text);

        document.Undo();
        Assert.Equal("hola", document.Text);
        document.Redo();
        Assert.Equal("Xola", document.Text);
    }

    [Fact]
    public void Commands_such_as_paste_always_insert()
    {
        var document = DocumentWith("hola");
        document.IsOverwrite = true;

        document.ApplyEdit(new TextEdit(0, 0, "pegado ", 7, 0));

        Assert.Equal("pegado hola", document.Text);
    }
}

public class FrontMatterTests
{
    private readonly MarkdownService _markdown = new();

    private const string Source = "---\ntitle: \"Mi documento\"\nauthor: Ana\ntags:\n  - uno\n  - dos\n---\n\n# Cuerpo\n\nTexto.";

    [Fact]
    public void Front_matter_is_shown_as_its_own_block_and_not_rendered_as_markdown()
    {
        var analysis = _markdown.Analyze(Source, new MarkdownRenderOptions());
        var html = analysis.Html();

        Assert.StartsWith("<table class=\"front-matter\">", analysis.Blocks[0].Html);
        Assert.Contains("<th>title</th><td>Mi documento</td>", html);
        Assert.Contains("<th>tags</th><td>- uno<br />- dos</td>", html);
        Assert.DoesNotContain("<hr", html);
        Assert.DoesNotContain("<h2", html);
        Assert.Contains("<h1 id=\"cuerpo\">Cuerpo</h1>", html);
        Assert.Equal(["Cuerpo"], analysis.Headings.Select(h => h.Text));
    }

    [Fact]
    public void Entries_are_available_by_key()
    {
        var frontMatter = _markdown.Analyze(Source, new MarkdownRenderOptions()).FrontMatter!;

        Assert.Equal("Mi documento", frontMatter["TITLE"]);
        Assert.Equal("Ana", frontMatter["author"]);
        Assert.Null(frontMatter["missing"]);
    }

    [Fact]
    public void Export_leaves_the_block_out()
    {
        var analysis = _markdown.Analyze(Source, new MarkdownRenderOptions(IncludeFrontMatter: false));

        Assert.NotNull(analysis.FrontMatter);
        Assert.DoesNotContain("front-matter", analysis.Html());
        Assert.StartsWith("<h1", analysis.Html());
    }

    [Fact]
    public void Its_content_is_escaped()
    {
        var html = _markdown.Analyze("---\ntitle: <script>alert(1)</script>\n---\n", new MarkdownRenderOptions()).Html();

        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void A_block_that_is_not_a_simple_mapping_is_shown_verbatim()
    {
        var analysis = _markdown.Analyze("---\n- solo\n- una lista\n---\ntexto", new MarkdownRenderOptions());

        Assert.StartsWith("<pre class=\"front-matter\">- solo\n- una lista</pre>", analysis.Blocks[0].Html);
        Assert.Empty(analysis.FrontMatter!.Entries);
    }

    [Fact]
    public void Dashes_later_in_the_document_are_still_a_horizontal_rule()
    {
        var analysis = _markdown.Analyze("texto\n\n---\n\nmás", new MarkdownRenderOptions());

        Assert.Null(analysis.FrontMatter);
        Assert.Contains("<hr />", analysis.Html());
    }

    [Fact]
    public void The_editor_styles_the_block_as_metadata()
    {
        var runs = _markdown.Analyze(Source, new MarkdownRenderOptions(IncludeSyntaxRuns: true)).Runs;

        Assert.Equal(SyntaxColor.Meta, runs[0].Color);
        Assert.Equal(0, runs[0].Start);
        Assert.True(runs[0].End >= Source.IndexOf("---\n\n# Cuerpo", StringComparison.Ordinal) + 3);
    }

    [Fact]
    public async Task Exported_page_takes_its_title_from_the_front_matter()
    {
        using var app = new TestApp();
        var path = app.WriteFile("nota.md", Source);
        await app.Main.InitializeAsync([path]);

        var html = app.Main.BuildExportHtml(app.Active, forPdf: false);

        Assert.Contains("<title>Mi documento</title>", html);
        Assert.DoesNotContain("class=\"front-matter\"", html);
    }
}

public class GlobalSearchTests
{
    private static async Task<TestApp> AppWithTabs()
    {
        var app = new TestApp();
        await app.Main.InitializeAsync(
        [
            app.WriteFile("a.txt", "uno dos\ntres uno"),
            app.WriteFile("b.md", "# Título\n\nnada aquí"),
            app.WriteFile("c.txt", "UNO al principio"),
        ]);
        return app;
    }

    [Fact]
    public async Task Finds_matches_in_every_tab_with_their_line_and_preview()
    {
        using var app = await AppWithTabs();
        var search = app.Main.GlobalSearch;
        search.Query = "uno";

        await search.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["a.txt : 1", "a.txt : 2", "c.txt : 1"], search.Results.Select(r => r.Location));
        Assert.Equal("tres uno", search.Results[1].Preview);
        Assert.Equal("GlobalSearch_Summary(3,2)", search.Summary);
    }

    [Fact]
    public async Task Options_narrow_the_search()
    {
        using var app = await AppWithTabs();
        var search = app.Main.GlobalSearch;
        search.Query = "uno";
        search.MatchCase = true;

        await search.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, search.Results.Count);
        Assert.All(search.Results, r => Assert.Equal("a.txt", r.DocumentName));
    }

    [Fact]
    public async Task Opening_a_result_activates_its_tab_and_selects_the_match()
    {
        using var app = await AppWithTabs();
        var search = app.Main.GlobalSearch;
        search.Query = "principio";
        await search.SearchCommand.ExecuteAsync(null);

        search.Open(search.Results.Single());

        Assert.Equal("c.txt", app.Active.DisplayName);
        Assert.Equal("principio", app.Active.SelectedText);
    }

    [Fact]
    public async Task Replace_all_asks_first_and_changes_every_tab_as_one_undo_step_each()
    {
        using var app = await AppWithTabs();
        var search = app.Main.GlobalSearch;
        search.Query = "uno";
        search.Replacement = "1";

        app.Dialogs.ConfirmAnswer = false;
        await search.ReplaceAllCommand.ExecuteAsync(null);
        Assert.Equal("uno dos\ntres uno", app.Main.Documents[0].Text);
        Assert.Equal("GlobalSearch_Confirm(3,2)", Assert.Single(app.Dialogs.Confirmations));

        app.Dialogs.ConfirmAnswer = true;
        await search.ReplaceAllCommand.ExecuteAsync(null);

        Assert.Equal("1 dos\ntres 1", app.Main.Documents[0].Text);
        Assert.Equal("# Título\n\nnada aquí", app.Main.Documents[1].Text);
        Assert.Equal("1 al principio", app.Main.Documents[2].Text);
        Assert.Equal([true, false, true], app.Main.Documents.Select(d => d.IsModified));
        Assert.Equal("GlobalSearch_Replaced(3,2)", search.Summary);

        app.Main.Documents[0].Undo();
        Assert.Equal("uno dos\ntres uno", app.Main.Documents[0].Text);
        Assert.Equal("uno dos\ntres uno", File.ReadAllText(app.Temp.File("a.txt")));
    }

    [Fact]
    public async Task Invalid_regex_and_no_matches_are_reported()
    {
        using var app = await AppWithTabs();
        var search = app.Main.GlobalSearch;

        search.Query = "inexistente";
        await search.SearchCommand.ExecuteAsync(null);
        Assert.Empty(search.Results);
        Assert.Equal("Find_NoResults", search.Summary);

        search.UseRegex = true;
        search.Query = "(";
        await search.SearchCommand.ExecuteAsync(null);
        Assert.True(search.HasError);
        Assert.Equal("Find_InvalidRegex", search.Summary);
    }

    [Fact]
    public async Task The_command_opens_the_panel_seeded_with_the_selection()
    {
        using var app = await AppWithTabs();
        app.Main.ActiveDocument = app.Main.Documents[0];
        app.Active.Select(4, 3);

        app.Main.FindInTabsCommand.Execute(null);

        Assert.True(app.Main.GlobalSearch.IsVisible);
        Assert.Equal("dos", app.Main.GlobalSearch.Query);
    }
}

public class ErrorLogTests
{
    [Fact]
    public void Errors_are_appended_with_time_and_context_and_can_be_cleared()
    {
        using var temp = new TempDirectory();
        var log = new ErrorLog(new AppPaths(temp.File("data")));
        Assert.False(log.HasEntries);
        Assert.Null(log.Read());

        log.Write(new InvalidOperationException("primero"));
        log.Write(new IOException("segundo"), "guardar");

        var text = log.Read()!;
        Assert.True(log.HasEntries);
        Assert.Contains("InvalidOperationException: primero", text);
        Assert.Contains("[guardar]", text);
        Assert.Contains("IOException: segundo", text);
        Assert.StartsWith(DateTimeOffset.Now.Year.ToString(), text);

        log.Clear();
        Assert.False(log.HasEntries);
    }

    [Fact]
    public async Task The_log_can_be_opened_in_a_tab_copied_and_deleted_from_settings()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.ErrorLog.Write(new InvalidOperationException("algo falló"));

        app.Main.OpenSettingsCommand.Execute(null);
        Assert.True(app.Main.HasErrorLog);

        app.Main.CopyErrorLogCommand.Execute(null);
        Assert.Contains("algo falló", app.Clipboard.Text);

        await app.Main.OpenErrorLogCommand.ExecuteAsync(null);
        Assert.False(app.Main.IsSettingsOpen);
        Assert.Equal("crash.log", app.Active.DisplayName);
        Assert.Contains("algo falló", app.Active.Text);

        app.Main.ClearErrorLogCommand.Execute(null);
        Assert.False(app.Main.HasErrorLog);
        Assert.False(File.Exists(app.Main.ErrorLogPath));
    }
}

public class SettingsDefaultsTests
{
    [Fact]
    public void New_options_have_safe_defaults_and_round_trip()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.File("data"));
        using (var first = new JsonSettingsService(paths))
        {
            Assert.False(first.Current.ShowInvisibles);
            Assert.Equal(SyntaxTheme.Default, first.Current.SyntaxTheme);
            first.Current.ShowInvisibles = true;
            first.Current.SyntaxTheme = SyntaxTheme.Accent;
            first.FlushAsync().GetAwaiter().GetResult();
        }

        using var second = new JsonSettingsService(paths);
        Assert.True(second.Current.ShowInvisibles);
        Assert.Equal(SyntaxTheme.Accent, second.Current.SyntaxTheme);
    }
}
