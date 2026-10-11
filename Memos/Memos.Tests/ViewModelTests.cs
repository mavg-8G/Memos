using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;
using System.Text;

namespace Memos.Tests;

public class DocumentViewModelTests
{
    [Fact]
    public void Edits_can_be_undone_and_redone_and_drive_the_modified_flag()
    {
        var document = TestHelpers.NewDocument();

        document.ApplyEdit(new TextEdit(0, 0, "hola\nmundo", 10, 0));
        document.Select(0, 4);
        document.ChangeCase(upper: true);

        Assert.Equal("HOLA\nmundo", document.Text);
        Assert.True(document.IsModified);

        document.Undo();
        Assert.Equal("hola\nmundo", document.Text);
        document.Undo();
        Assert.Equal("", document.Text);
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);

        document.Redo();
        document.Redo();
        Assert.Equal("HOLA\nmundo", document.Text);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void Caret_position_and_statistics_follow_the_text()
    {
        var document = TestHelpers.NewDocument();
        document.ApplyEdit(new TextEdit(0, 0, "primera línea\nsegunda 👨‍👩‍👧‍👦 x", 0, 0));

        document.Select(document.Text.Length, 0);

        Assert.Equal(2, document.LineCount);
        Assert.Equal((2, 12), (document.Line, document.Column));
        Assert.Equal(4, document.WordCount);
        Assert.Equal(24, document.CharacterCount);

        document.GoToLine(1);
        Assert.Equal((1, 1), (document.Line, document.Column));
    }

    [Fact]
    public void Changing_encoding_or_line_ending_marks_the_document_modified()
    {
        var document = TestHelpers.NewDocument();

        document.SetLineEnding(LineEnding.Lf);
        Assert.True(document.IsModified);

        document.MarkSaved(Path.Combine(Path.GetTempPath(), "x.txt"), default);
        Assert.False(document.IsModified);

        document.SetEncoding(TextEncodingId.Utf16Le);
        Assert.True(document.IsModified);
    }

    [Fact]
    public void Tab_inserts_spaces_to_the_next_stop_or_indents_a_block()
    {
        var document = TestHelpers.NewDocument(settings: new AppSettings { InsertSpaces = true, TabSize = 4 });
        document.ApplyEdit(new TextEdit(0, 0, "ab\ncd", 2, 0));

        document.HandleTab(shift: false);
        Assert.Equal("ab  \ncd", document.Text);

        document.Select(0, document.Text.Length);
        document.HandleTab(shift: false);
        Assert.Equal("    ab  \n    cd", document.Text);

        document.HandleTab(shift: true);
        Assert.Equal("ab  \ncd", document.Text);
    }

    [Fact]
    public void Enter_keeps_the_indentation_of_the_current_line()
    {
        var document = TestHelpers.NewDocument(settings: new AppSettings { AutoIndent = true });
        document.ApplyEdit(new TextEdit(0, 0, "\t  item", 7, 0));

        Assert.True(document.HandleEnter());

        Assert.Equal("\t  item\n\t  ", document.Text);
        Assert.Equal(document.Text.Length, document.SelectionStart);
    }

    [Fact]
    public async Task Markdown_documents_produce_an_outline_and_text_documents_do_not()
    {
        var document = TestHelpers.NewDocument(DocumentKind.Markdown);
        document.ApplyEdit(new TextEdit(0, 0, "# Uno\n\n## Dos", 0, 0));
        await document.PendingAnalysis;

        Assert.Equal(["Uno", "Dos"], document.Headings.Select(h => h.Text));
        Assert.NotNull(document.Analysis);

        document.Kind = DocumentKind.Text;

        Assert.Empty(document.Headings);
        Assert.Null(document.Analysis);
    }

    [Fact]
    public void Markdown_commands_write_standard_markup()
    {
        var document = TestHelpers.NewDocument(DocumentKind.Markdown);
        document.ApplyEdit(new TextEdit(0, 0, "título", 0, 6));

        document.ToggleBold();
        document.SetHeading(2);

        Assert.Equal("## **título**", document.Text);
    }

    [Fact]
    public void Zoom_moves_in_steps_of_ten_within_limits()
    {
        var document = TestHelpers.NewDocument();
        document.ZoomPercent = 95;

        document.ZoomIn();
        Assert.Equal(100, document.ZoomPercent);
        document.ZoomOut();
        Assert.Equal(90, document.ZoomPercent);

        document.ZoomPercent = AppSettings.MaxZoom;
        document.ZoomIn();
        Assert.Equal(AppSettings.MaxZoom, document.ZoomPercent);
    }
}

public class FindReplaceViewModelTests
{
    private static async Task<DocumentViewModel> DocumentWith(string text, string query)
    {
        var document = TestHelpers.NewDocument();
        document.ApplyEdit(new TextEdit(0, 0, text, 0, 0));
        document.Find.Show(replace: true);
        document.Find.Query = query;
        await document.Find.Pending;
        return document;
    }

    [Fact]
    public async Task Typing_a_query_selects_the_first_match_and_counts_them()
    {
        var document = await DocumentWith("uno dos uno tres uno", "uno");

        Assert.Equal(3, document.Find.Matches.Count);
        Assert.Equal(0, document.Find.CurrentIndex);
        Assert.Equal((0, 3), (document.SelectionStart, document.SelectionLength));
        Assert.Equal("Find_Count(1,3)", document.Find.StatusText);
    }

    [Fact]
    public async Task Next_and_previous_wrap_around()
    {
        var document = await DocumentWith("uno dos uno tres uno", "uno");
        var find = document.Find;

        find.FindNext();
        Assert.Equal(8, document.SelectionStart);
        find.FindNext();
        find.FindNext();
        Assert.Equal(0, document.SelectionStart);
        find.FindPrevious();
        Assert.Equal(17, document.SelectionStart);
        Assert.Equal(2, find.CurrentIndex);
    }

    [Fact]
    public async Task Replace_changes_the_current_match_and_moves_on()
    {
        var document = await DocumentWith("uno dos uno", "uno");
        document.Find.Replacement = "1";

        await document.Find.ReplaceCurrentAsync();

        Assert.Equal("1 dos uno", document.Text);
        Assert.Equal((6, 3), (document.SelectionStart, document.SelectionLength));
        Assert.Single(document.Find.Matches);
    }

    [Fact]
    public async Task Replace_all_is_a_single_undo_step()
    {
        var document = await DocumentWith("uno dos uno tres uno", "uno");
        document.Find.Replacement = "1";

        await document.Find.ReplaceAllAsync();

        Assert.Equal("1 dos 1 tres 1", document.Text);
        Assert.Equal("Find_Replaced(3)", document.Find.StatusText);

        document.Undo();
        Assert.Equal("uno dos uno tres uno", document.Text);
    }

    [Fact]
    public async Task Invalid_regex_shows_an_error_and_no_matches()
    {
        var document = await DocumentWith("abc", "a");
        document.Find.UseRegex = true;
        document.Find.Query = "(";
        await document.Find.Pending;

        Assert.True(document.Find.HasError);
        Assert.Equal("Find_InvalidRegex", document.Find.StatusText);
        Assert.Empty(document.Find.Matches);
    }

    [Fact]
    public async Task Editing_the_document_refreshes_the_results()
    {
        var document = await DocumentWith("uno", "uno");

        document.ApplyEdit(new TextEdit(3, 0, " uno", 7, 0));
        await document.Find.Pending;

        Assert.Equal(2, document.Find.Matches.Count);
    }
}

public class MainViewModelTests
{
    [Fact]
    public async Task Starts_with_one_blank_document_of_the_configured_kind()
    {
        using var app = new TestApp();
        app.Settings.StartupDocument = DocumentKind.Markdown;

        await app.Main.InitializeAsync([]);

        Assert.Single(app.Main.Documents);
        Assert.True(app.Active.IsPristine);
        Assert.True(app.Main.IsMarkdownActive);
        Assert.Equal("Untitled", app.Active.DisplayName);
    }

    [Fact]
    public async Task Opening_files_replaces_the_blank_tab_picks_the_workspace_and_does_not_duplicate()
    {
        using var app = new TestApp();
        var text = app.WriteFile("notas.txt", "hola");
        var markdown = app.WriteFile("LEEME.MD", "# hola");
        await app.Main.InitializeAsync([]);

        await app.Main.OpenPathsAsync([text, markdown, text]);

        Assert.Equal(2, app.Main.Documents.Count);
        Assert.Equal(DocumentKind.Text, app.Main.Documents[0].Kind);
        Assert.Equal(DocumentKind.Markdown, app.Main.Documents[1].Kind);
        Assert.Equal(text, app.Active.FilePath);
        Assert.False(app.Active.IsModified);
        Assert.Equal([markdown, text], app.Recent.Items);
    }

    [Fact]
    public async Task Missing_and_binary_files_are_reported_without_opening_a_tab()
    {
        using var app = new TestApp();
        var binary = app.Temp.File("datos.bin");
        File.WriteAllBytes(binary, [1, 0, 2, 0, 0, 0, 3]);
        await app.Main.InitializeAsync([]);

        await app.Main.OpenPathsAsync([app.Temp.File("no-existe.txt"), binary]);

        Assert.Single(app.Main.Documents);
        Assert.True(app.Active.IsPristine);
        Assert.Equal(2, app.Dialogs.Messages.Count);
        Assert.StartsWith("Msg_FileNotFound", app.Dialogs.Messages[0]);
        Assert.StartsWith("Msg_Binary", app.Dialogs.Messages[1]);
    }

    [Fact]
    public async Task Save_writes_the_file_with_its_encoding_and_line_endings()
    {
        using var app = new TestApp();
        var path = app.Temp.File("salida.txt");
        File.WriteAllBytes(path, TextEncodings.Encode("uno\r\ndos", TextEncodingId.Utf16Le));
        await app.Main.InitializeAsync([path]);

        app.Active.Select(app.Active.Text.Length, 0);
        app.Type("\ntres 👍");
        await app.Main.SaveCommand.ExecuteAsync(null);

        Assert.False(app.Active.IsModified);
        Assert.Equal(TextEncodings.Encode("uno\r\ndos\r\ntres 👍", TextEncodingId.Utf16Le), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Saving_an_untitled_document_asks_for_a_name_and_cancelling_keeps_it_modified()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Type("contenido");

        await app.Main.SaveCommand.ExecuteAsync(null);
        Assert.True(app.Active.IsModified);
        Assert.Null(app.Active.FilePath);

        app.Pickers.SaveAnswer = app.Temp.File("nuevo.md");
        await app.Main.SaveCommand.ExecuteAsync(null);

        Assert.False(app.Active.IsModified);
        Assert.Equal("nuevo.md", app.Active.DisplayName);
        Assert.Equal(DocumentKind.Markdown, app.Active.Kind);
        Assert.Equal("contenido", File.ReadAllText(app.Pickers.SaveAnswer));
    }

    [Theory]
    [InlineData(SaveChangesChoice.Cancel, true, "original")]
    [InlineData(SaveChangesChoice.Discard, false, "original")]
    [InlineData(SaveChangesChoice.Save, false, "original editado")]
    public async Task Closing_a_modified_tab_asks_and_respects_the_answer(SaveChangesChoice answer, bool staysOpen, string onDisk)
    {
        using var app = new TestApp();
        var path = app.WriteFile("doc.txt", "original");
        await app.Main.InitializeAsync([path]);
        var document = app.Active;
        document.Select(document.Text.Length, 0);
        app.Type(" editado");
        app.Dialogs.SaveChangesAnswer = answer;

        var closed = await app.Main.CloseDocumentAsync(document);

        Assert.Equal(1, app.Dialogs.SaveChangesPrompts);
        Assert.Equal(!staysOpen, closed);
        Assert.Equal(staysOpen, app.Main.Documents.Contains(document));
        Assert.Equal(onDisk, File.ReadAllText(path));
        Assert.Single(app.Main.Documents); // a blank tab takes over when the last one closes
    }

    [Fact]
    public async Task Closing_an_unmodified_tab_does_not_ask()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([app.WriteFile("doc.txt", "x")]);

        Assert.True(await app.Main.CloseDocumentAsync(app.Active));
        Assert.Equal(0, app.Dialogs.SaveChangesPrompts);
    }

    [Fact]
    public async Task Closing_the_window_asks_for_every_modified_document_and_can_be_cancelled()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([app.WriteFile("a.txt", "a"), app.WriteFile("b.txt", "b")]);
        foreach (var document in app.Main.Documents)
        {
            document.ApplyEdit(new TextEdit(0, 0, "x", 1, 0));
        }

        app.Dialogs.SaveChangesAnswer = SaveChangesChoice.Cancel;
        Assert.False(await app.Main.PrepareToCloseAsync());
        Assert.Equal(1, app.Dialogs.SaveChangesPrompts);

        app.Dialogs.SaveChangesAnswer = SaveChangesChoice.Discard;
        Assert.True(await app.Main.PrepareToCloseAsync());
        Assert.Equal(3, app.Dialogs.SaveChangesPrompts);
        Assert.Equal("a", File.ReadAllText(app.Temp.File("a.txt")));
    }

    [Fact]
    public async Task Tab_commands_close_others_close_to_the_right_and_reopen()
    {
        using var app = new TestApp();
        var paths = new[] { "a.txt", "b.txt", "c.txt", "d.txt" }.Select(n => app.WriteFile(n, n)).ToArray();
        await app.Main.InitializeAsync(paths);

        await app.Main.CloseToRightCommand.ExecuteAsync(app.Main.Documents[1]);
        Assert.Equal(["a.txt", "b.txt"], app.Main.Documents.Select(d => d.DisplayName));

        await app.Main.CloseOthersCommand.ExecuteAsync(app.Main.Documents[1]);
        Assert.Equal(["b.txt"], app.Main.Documents.Select(d => d.DisplayName));

        await app.Main.ReopenClosedCommand.ExecuteAsync(null);
        Assert.Equal(["b.txt", "a.txt"], app.Main.Documents.Select(d => d.DisplayName));
        Assert.Equal("a.txt", app.Active.DisplayName);

        app.Main.NextTabCommand.Execute(null);
        Assert.Equal("b.txt", app.Active.DisplayName);
        app.Main.MoveDocument(0, 1);
        Assert.Equal(["a.txt", "b.txt"], app.Main.Documents.Select(d => d.DisplayName));
    }

    [Fact]
    public async Task Reopening_a_closed_unsaved_tab_brings_its_text_back()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Type("borrador sin guardar");
        app.Dialogs.SaveChangesAnswer = SaveChangesChoice.Discard;

        await app.Main.CloseTabCommand.ExecuteAsync(null);
        Assert.True(app.Active.IsPristine);
        await app.Main.ReopenClosedCommand.ExecuteAsync(null);

        Assert.Equal("borrador sin guardar", app.Active.Text);
        Assert.True(app.Active.IsModified);
        Assert.False(app.Main.ReopenClosedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Session_restores_tabs_unsaved_text_caret_and_active_tab()
    {
        using var temp = new TempDirectory();
        string saved;
        using (var first = new TestApp(temp))
        {
            first.Settings.RestoreSession = true;
            saved = first.WriteFile("guardado.md", "# guardado");
            var edited = first.WriteFile("editado.txt", "en disco");
            await first.Main.InitializeAsync([saved, edited]);
            first.Active.Select(first.Active.Text.Length, 0);
            first.Type(" + cambios sin guardar 👍");
            first.Main.NewTextCommand.Execute(null);
            first.Type("nota rápida");
            first.Main.ActiveDocument = first.Main.Documents[1];
            first.Active.Select(3, 2);

            Assert.True(await first.Main.PrepareToCloseAsync());
            Assert.Equal(0, first.Dialogs.SaveChangesPrompts);
            await first.SettingsService.FlushAsync();
        }

        using var second = new TestApp(temp);
        await second.Main.InitializeAsync([]);

        Assert.True(second.Settings.RestoreSession);
        Assert.Equal(["guardado.md", "editado.txt", "Untitled"], second.Main.Documents.Select(d => d.DisplayName));
        Assert.Equal([false, true, true], second.Main.Documents.Select(d => d.IsModified));
        Assert.Equal("en disco + cambios sin guardar 👍", second.Main.Documents[1].Text);
        Assert.Equal("nota rápida", second.Main.Documents[2].Text);
        Assert.Equal(DocumentKind.Markdown, second.Main.Documents[0].Kind);
        Assert.Same(second.Main.Documents[1], second.Active);
        Assert.Equal((3, 2), (second.Active.SelectionStart, second.Active.SelectionLength));
        Assert.Equal("en disco", File.ReadAllText(temp.File("editado.txt")));
    }

    [Fact]
    public async Task Without_session_restore_nothing_is_brought_back()
    {
        using var temp = new TempDirectory();
        using (var first = new TestApp(temp))
        {
            await first.Main.InitializeAsync([first.WriteFile("a.txt", "a")]);
            Assert.True(await first.Main.PrepareToCloseAsync());
        }

        using var second = new TestApp(temp);
        await second.Main.InitializeAsync([]);

        Assert.Single(second.Main.Documents);
        Assert.True(second.Active.IsPristine);
    }

    [Fact]
    public async Task A_file_changed_by_another_program_is_announced_and_not_overwritten_silently()
    {
        using var app = new TestApp();
        var path = app.WriteFile("compartido.txt", "v1");
        await app.Main.InitializeAsync([path]);
        app.Type("mío ");
        File.WriteAllText(path, "v2 de otro programa");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        app.Main.CheckExternalChanges();
        Assert.Equal("Notice_ExternalChange", app.Active.Notice?.Message);

        app.Dialogs.ConfirmAnswer = false;
        await app.Main.SaveCommand.ExecuteAsync(null);
        Assert.Equal("v2 de otro programa", File.ReadAllText(path));
        Assert.True(app.Active.IsModified);

        app.Dialogs.ConfirmAnswer = true;
        await app.Main.SaveCommand.ExecuteAsync(null);
        Assert.Equal("mío v1", File.ReadAllText(path));
        Assert.False(app.Active.IsModified);
    }

    [Fact]
    public async Task Reloading_from_the_notice_takes_the_content_on_disk()
    {
        using var app = new TestApp();
        var path = app.WriteFile("log.txt", "v1");
        await app.Main.InitializeAsync([path]);
        File.WriteAllText(path, "v2");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        app.Main.CheckExternalChanges();
        await app.Active.Notice!.Action!();

        Assert.Equal("v2", app.Active.Text);
        Assert.False(app.Active.IsModified);
    }

    [Fact]
    public async Task Lossy_encoding_conversion_needs_confirmation()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Type("日本語");

        app.Dialogs.ConfirmAnswer = false;
        await app.Main.SetEncodingCommand.ExecuteAsync(TextEncodingId.Windows1252);
        Assert.Equal(TextEncodingId.Utf8, app.Active.Encoding);
        Assert.Single(app.Dialogs.Confirmations);

        await app.Main.SetEncodingCommand.ExecuteAsync(TextEncodingId.Utf16Be);
        Assert.Equal(TextEncodingId.Utf16Be, app.Active.Encoding);
        Assert.Single(app.Dialogs.Confirmations);
    }

    [Fact]
    public async Task Reopening_with_another_encoding_reinterprets_the_file()
    {
        using var app = new TestApp();
        var path = app.Temp.File("ansi.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("ñandú"));
        await app.Main.InitializeAsync([path]);

        await app.Main.ReopenWithEncodingCommand.ExecuteAsync(TextEncodingId.Windows1252);

        Assert.Equal("Ã±andÃº", app.Active.Text);
        Assert.Equal(TextEncodingId.Windows1252, app.Active.Encoding);
        Assert.False(app.Active.IsModified);
    }

    [Fact]
    public async Task Read_only_files_report_an_error_and_stay_modified()
    {
        using var app = new TestApp();
        var path = app.WriteFile("fijo.txt", "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        await app.Main.InitializeAsync([path]);
        app.Type("x");

        await app.Main.SaveCommand.ExecuteAsync(null);

        Assert.True(app.Active.IsModified);
        Assert.StartsWith("Msg_ReadOnly", Assert.Single(app.Dialogs.Messages));
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public async Task Warnings_about_the_file_are_attached_to_the_document()
    {
        using var app = new TestApp();
        var mixed = app.Temp.File("mixto.txt");
        File.WriteAllText(mixed, "a\r\nb\nc\r\n");
        var ansi = app.Temp.File("ansi.txt");
        File.WriteAllBytes(ansi, [0xE1, 0xE9]);
        await app.Main.InitializeAsync([mixed, ansi]);

        Assert.Equal("Notice_MixedLineEndings(CRLF)", app.Main.Documents[0].Notice?.Message);
        Assert.Equal("Notice_EncodingGuessed(Windows-1252)", app.Main.Documents[1].Notice?.Message);
    }

    [Fact]
    public async Task Markdown_exports_html_and_hands_pdf_to_the_exporter()
    {
        using var app = new TestApp();
        var path = app.WriteFile("informe.md", "# Informe\n\n![logo](logo.png)\n\n<script>alert(1)</script>\n");
        await app.Main.InitializeAsync([path]);

        app.Pickers.ExportAnswer = app.Temp.File("informe.html");
        await app.Main.ExportHtmlCommand.ExecuteAsync(null);
        var html = File.ReadAllText(app.Pickers.ExportAnswer);

        Assert.Contains("<title>informe</title>", html);
        Assert.Contains("<h1 id=\"informe\">Informe</h1>", html);
        Assert.Contains("src=\"logo.png\"", html);
        Assert.DoesNotContain("<script", html);

        app.Pickers.ExportAnswer = app.Temp.File("informe.pdf");
        await app.Main.ExportPdfCommand.ExecuteAsync(null);

        Assert.Equal(app.Pickers.ExportAnswer, app.Pdf.OutputPath);
        Assert.Contains("color-scheme:light;", app.Pdf.Html);
        Assert.Contains("src=\"https://memos-doc.example/logo.png\"", app.Pdf.Html);

        app.Main.CopyHtmlCommand.Execute(null);
        Assert.StartsWith("<h1 id=\"informe\">Informe</h1>", app.Clipboard.Html);
    }

    [Fact]
    public async Task Markdown_commands_are_only_available_in_the_markdown_workspace()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);

        Assert.False(app.Main.BoldCommand.CanExecute(null));
        Assert.False(app.Main.ExportPdfCommand.CanExecute(null));

        app.Main.SetKindCommand.Execute(DocumentKind.Markdown);

        Assert.True(app.Main.IsMarkdownActive);
        Assert.True(app.Main.BoldCommand.CanExecute(null));
        Assert.True(app.Main.ExportPdfCommand.CanExecute(null));
    }

    [Fact]
    public async Task Auto_save_never_prompts()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Type("sin nombre");

        Assert.False(await app.Main.SaveDocumentAsync(app.Active, saveAs: false, quiet: true));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Dialogs.Confirmations);
    }
}

public class PersistenceTests
{
    [Fact]
    public async Task Settings_are_stored_and_read_back()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.File("data"));
        using (var first = new JsonSettingsService(paths))
        {
            first.Current.Theme = AppTheme.Dark;
            first.Current.FontFamily = "Consolas";
            first.Current.TabSize = 2;
            first.Current.MarkdownViewMode = MarkdownViewMode.Preview;
            first.Current.Language = "es-ES";
            await first.FlushAsync();
        }

        using var second = new JsonSettingsService(paths);

        Assert.Equal(AppTheme.Dark, second.Current.Theme);
        Assert.Equal("Consolas", second.Current.FontFamily);
        Assert.Equal(2, second.Current.TabSize);
        Assert.Equal(MarkdownViewMode.Preview, second.Current.MarkdownViewMode);
        Assert.Equal("es-ES", second.Current.Language);
    }

    [Fact]
    public void Corrupt_or_out_of_range_settings_fall_back_to_safe_values()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.File("data"));
        File.WriteAllText(temp.File(@"data\settings.json"), "{ \"FontSize\": 9999, \"TabSize\": 0, \"Theme\": \"Dark\" }");
        using var clamped = new JsonSettingsService(new AppPaths(temp.File("data")));

        Assert.Equal(AppSettings.MaxFontSize, clamped.Current.FontSize);
        Assert.Equal(1, clamped.Current.TabSize);
        Assert.Equal(AppTheme.Dark, clamped.Current.Theme);

        File.WriteAllText(temp.File(@"data\settings.json"), "{ not json");
        using var defaults = new JsonSettingsService(new AppPaths(temp.File("data")));

        Assert.Equal(AppTheme.System, defaults.Current.Theme);
    }

    [Fact]
    public void Recent_files_keep_the_latest_first_without_duplicates()
    {
        using var temp = new TempDirectory();
        var recent = new RecentFilesService(new AppPaths(temp.File("data")));

        recent.Add(@"C:\a.txt");
        recent.Add(@"C:\b.txt");
        recent.Add(@"c:\A.TXT");

        Assert.Equal([@"c:\A.TXT", @"C:\b.txt"], recent.Items);

        recent.Remove(@"C:\b.txt");
        Assert.Single(recent.Items);
        recent.Clear();
        Assert.Empty(recent.Items);
    }
}
