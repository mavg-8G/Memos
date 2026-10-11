using System.Text;
using System.Windows.Automation;

namespace Memos.UITests;

/// <summary>
/// Tests of the views: the real window is started and driven the way a user (or a screen reader)
/// would, and results are checked on screen and on disk.
/// </summary>
public class WindowTests
{
    private const int VkInsert = 0x2D;
    private const int VkEnter = 0x0D;
    private const int VkTab = 0x09;

    private static void WaitForText(MemosApp app, string automationId, string expected) =>
        MemosApp.Eventually(() => app.TryFind(automationId)?.Current.Name == expected,
            $"'{automationId}' to show \"{expected}\" (it shows \"{app.TryFind(automationId)?.Current.Name}\")");

    [Fact]
    public void Starts_with_one_blank_tab_in_insert_mode()
    {
        using var app = MemosApp.Launch();

        Assert.Equal(["Untitled"], app.TabNames());
        WaitForText(app, "CharactersText", "0 characters");
        Assert.Equal("INS", app.Text("InsertModeButton"));
        Assert.Equal("Ln 1, Col 1", app.Text("PositionButton"));
        Assert.Equal("UTF-8", app.Text("EncodingButton"));
    }

    [Fact]
    public void Typing_updates_the_status_bar_and_tab_and_enter_work()
    {
        using var app = MemosApp.Launch();

        app.Type("año 👍");
        WaitForText(app, "CharactersText", "5 characters");
        WaitForText(app, "WordsText", "1 words");

        app.Press(VkEnter);
        app.Press(VkTab);
        app.Type("x");

        // Tab inserts spaces up to the next stop (4 by default) and the caret ends after the x.
        WaitForText(app, "CharactersText", "10 characters");
        WaitForText(app, "PositionButton", "Ln 2, Col 6");
    }

    [Fact]
    public void Files_given_at_start_open_as_tabs_with_the_right_workspace()
    {
        using var app = MemosApp.Launch(docs =>
        [
            MemosApp.WriteDoc(docs, "notas.txt", "uno\r\ndos"),
            MemosApp.WriteDoc(docs, "leeme.md", "# Hola\n\nmundo"),
        ]);

        Assert.Equal(["notas.txt", "leeme.md"], app.TabNames());

        // The Markdown tab is active: its tools are there and the file's LF endings were detected.
        Assert.NotNull(app.FindByName("Format", ControlType.MenuItem));
        WaitForText(app, "LineEndingButton", "LF");

        app.SelectTab("notas.txt");
        WaitForText(app, "LineEndingButton", "CRLF");
        MemosApp.Eventually(() => app.TryFindByName("Format", ControlType.MenuItem) is null, "the Format menu to disappear for plain text");
    }

    [Fact]
    public void Menu_commands_edit_the_text_and_can_be_undone()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "lista.txt", "uno\r\ndos")]);
        WaitForText(app, "CharactersText", "6 characters");

        app.Menu("Edit", "Lines", "Duplicate line");
        WaitForText(app, "CharactersText", "9 characters");

        app.Menu("Edit", "Undo");
        WaitForText(app, "CharactersText", "6 characters");

        app.Menu("Edit", "Redo");
        WaitForText(app, "CharactersText", "9 characters");
    }

    [Fact]
    public void Saving_writes_exactly_what_was_typed_keeping_encoding_and_line_endings()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "guardar.txt", "uno\r\ndos")]);
        WaitForText(app, "CharactersText", "6 characters");

        app.Type("ñ¿ ");
        WaitForText(app, "CharactersText", "9 characters");
        app.Menu("File", "Save");

        var path = app.Doc("guardar.txt");
        MemosApp.Eventually(() => File.ReadAllText(path, Encoding.UTF8) == "ñ¿ uno\r\ndos", "the file to be saved");
        Assert.Equal(Encoding.UTF8.GetBytes("ñ¿ uno\r\ndos"), File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Insert_key_switches_to_overwrite_and_typing_replaces_characters()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "sobre.txt", "hola mundo")]);
        WaitForText(app, "CharactersText", "10 characters");

        app.Press(VkInsert);
        WaitForText(app, "InsertModeButton", "OVR");

        app.Type("HO");
        WaitForText(app, "PositionButton", "Ln 1, Col 3");
        Assert.Equal("10 characters", app.Text("CharactersText"));

        app.Press(VkInsert);
        WaitForText(app, "InsertModeButton", "INS");
        app.Type("!");
        WaitForText(app, "CharactersText", "11 characters");

        app.Menu("File", "Save");
        MemosApp.Eventually(() => File.ReadAllText(app.Doc("sobre.txt")) == "HO!la mundo", "the overwritten text to be saved");
    }

    [Fact]
    public void Find_bar_counts_matches_and_moves_between_them()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "buscar.txt", "uno dos uno\r\ntres uno")]);

        app.Menu("Edit", "Find");
        app.SetText("QueryBox", "uno");
        WaitForText(app, "FindStatus", "1 of 3");

        MemosApp.Invoke(app.FindByName("Next (Enter)", ControlType.Button));
        WaitForText(app, "FindStatus", "2 of 3");
        WaitForText(app, "PositionButton", "Ln 1, Col 12");

        app.SetText("QueryBox", "no está");
        WaitForText(app, "FindStatus", "No results");
    }

    [Fact]
    public void Find_in_all_tabs_lists_results_and_replaces_after_confirmation()
    {
        using var app = MemosApp.Launch(docs =>
        [
            MemosApp.WriteDoc(docs, "a.txt", "uno dos"),
            MemosApp.WriteDoc(docs, "b.txt", "nada"),
            MemosApp.WriteDoc(docs, "c.txt", "tres uno\r\nuno"),
        ]);

        app.Menu("Edit", "Find in all tabs…");
        app.SetText("GlobalQueryBox", "uno");
        app.Click("GlobalSearchButton");
        WaitForText(app, "GlobalSummaryText", "3 matches in 2 tabs");
        Assert.NotNull(app.FindByName("c.txt : 2"));

        // Opening a result brings its tab forward and puts the caret on the match.
        MemosApp.Invoke(app.FindByName("c.txt : 1", ControlType.ListItem));
        WaitForText(app, "PositionButton", "Ln 1, Col 9");

        app.SetText("GlobalReplaceBox", "1");
        app.Click("GlobalReplaceAllButton");
        MemosApp.Invoke(app.Find("PrimaryButton"));
        WaitForText(app, "GlobalSummaryText", "3 replaced in 2 tabs");

        // Replaced in memory, as one undoable step per tab; nothing is written until the user saves.
        WaitForText(app, "CharactersText", "7 characters");
        Assert.Equal("tres uno\r\nuno", File.ReadAllText(app.Doc("c.txt")));
    }

    [Fact]
    public void Closing_with_unsaved_changes_asks_and_discarding_leaves_the_file_alone()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "cambios.txt", "original")]);
        WaitForText(app, "CharactersText", "8 characters");
        app.Type("x");
        WaitForText(app, "CharactersText", "9 characters");

        app.RequestClose();
        var cancel = app.FindByName("Cancel", ControlType.Button);
        Assert.False(app.HasExited);
        MemosApp.Invoke(cancel);
        MemosApp.Eventually(() => app.TryFindByName("Cancel", ControlType.Button) is null, "the dialog to close");
        Thread.Sleep(500);

        app.RequestClose();
        MemosApp.Invoke(app.FindByName("Don't save", ControlType.Button));

        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(15)));
        Assert.Equal("original", File.ReadAllText(app.Doc("cambios.txt")));
    }

    [Fact]
    public void A_missing_file_at_start_is_reported_and_the_app_keeps_running()
    {
        using var app = MemosApp.Launch(docs => [Path.Combine(docs, "no-existe.txt")]);

        MemosApp.Invoke(app.FindByName("OK", ControlType.Button));

        MemosApp.Eventually(() => app.TabNames().SequenceEqual(["Untitled"]), "a blank tab to take the place of the missing file");
        Assert.False(app.HasExited);
    }

    [Fact]
    public void A_second_launch_hands_its_file_to_the_open_window()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "primero.txt", "1")]);
        File.WriteAllText(app.Doc("segundo.md"), "# dos");

        app.LaunchAgain(app.Doc("segundo.md"));

        MemosApp.Eventually(() => app.TabNames().SequenceEqual(["primero.txt", "segundo.md"]), "the second file to open as a tab");
    }

    [Fact]
    public void Front_matter_stays_out_of_the_table_of_contents()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "meta.md", "---\ntitle: Informe\n---\n\n# Resumen\n\n## Detalle\n")]);

        app.Menu("View", "Table of contents");

        var outline = app.Find("OutlineList");
        MemosApp.Eventually(() => outline.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count == 2, "two headings in the outline");
        Assert.NotNull(app.FindByName("Resumen", ControlType.Text));
        Assert.Null(app.TryFindByName("title: Informe", ControlType.Text));
    }

    [Fact]
    public void Settings_show_the_new_options_and_apply_them_immediately()
    {
        using var app = MemosApp.Launch();

        MemosApp.Invoke(app.FindByName("Settings", ControlType.Button));

        Assert.NotNull(app.FindByName("Highlighting colours", ControlType.ComboBox));
        Assert.NotNull(app.FindByName("No errors have been recorded.", ControlType.Text));
        Assert.False(app.Find("ErrorLogOpenButton").Current.IsEnabled);

        // Turning the option on is written to the settings file without leaving the page.
        MemosApp.Invoke(app.Find("InvisiblesSwitch"));
        MemosApp.Eventually(() => File.ReadAllText(Path.Combine(app.DataDirectory, "settings.json")).Contains("\"ShowInvisibles\": true"), "the setting to be saved");

        MemosApp.Invoke(app.Find("BackButton"));
        Assert.NotNull(app.Find("CharactersText"));
        Assert.False(app.HasExited);
    }

    [Fact]
    public void An_existing_error_log_can_be_opened_from_settings()
    {
        string? logPath = null;
        using var app = MemosApp.Launch(docs =>
        {
            logPath = Path.Combine(Path.GetDirectoryName(docs)!, "data", "crash.log");
            File.WriteAllText(logPath, "2026-01-01T00:00:00  [UI]\nSystem.InvalidOperationException: prueba\n\n");
            return [];
        });

        MemosApp.Invoke(app.FindByName("Settings", ControlType.Button));
        MemosApp.Eventually(() => app.Find("ErrorLogOpenButton").Current.IsEnabled, "the error log buttons to be enabled");

        app.Click("ErrorLogOpenButton");
        MemosApp.Eventually(() => app.TabNames().Contains("crash.log"), "the log to open in a tab");

        MemosApp.Invoke(app.FindByName("Settings", ControlType.Button));
        app.Click("ErrorLogClearButton");
        MemosApp.Eventually(() => !File.Exists(logPath), "the log file to be deleted");
    }

    private const string SampleRtf =
        @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Times New Roman;}}\f0\fs28 \b negrita\b0  normal\par}";

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void An_rtf_file_opens_as_rich_text_and_is_not_modified_by_being_shown(string theme)
    {
        using var app = MemosApp.Launch(
            docs => [MemosApp.WriteDoc(docs, "carta.rtf", SampleRtf)],
            $$"""{ "Language": "en-US", "Theme": "{{theme}}" }""");

        Assert.Equal(["carta.rtf"], app.TabNames());
        WaitForText(app, "CharactersText", "14 characters");
        WaitForText(app, "WordsText", "2 words");

        // Encoding, line ending and zoom belong to plain text; a rich document has its own formatting tools.
        MemosApp.Eventually(() => app.TryFind("EncodingButton") is null && app.TryFind("ZoomButton") is null, "the plain-text status buttons to hide");
        Assert.NotNull(app.FindByName("Format", ControlType.MenuItem));
        Thread.Sleep(1000);

        // Nothing was edited, so closing must not ask and the file must be byte for byte the same.
        app.RequestClose();
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(15)), "An untouched RTF file should close without asking to save.");
        Assert.Equal(SampleRtf, File.ReadAllText(app.Doc("carta.rtf")));
    }

    [Fact]
    public void Editing_rich_text_keeps_its_fonts_and_styles_and_tab_undo_and_redo_work()
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, "carta.rtf", SampleRtf)]);
        WaitForText(app, "CharactersText", "14 characters");

        // Tab types a tab instead of moving the focus out of the editor.
        app.Press(VkTab);
        WaitForText(app, "CharactersText", "15 characters");
        app.Type("x");
        WaitForText(app, "CharactersText", "16 characters");

        app.Menu("Edit", "Undo");
        MemosApp.Eventually(() => app.Text("CharactersText") != "16 characters", "undo to take the typing back");
        app.Menu("Edit", "Redo");
        WaitForText(app, "CharactersText", "16 characters");

        app.Menu("Format", "Underline");
        app.Menu("File", "Save");

        var path = app.Doc("carta.rtf");
        MemosApp.Eventually(() => File.ReadAllText(path).Contains(@"\tab"), "the document to be saved");
        var saved = File.ReadAllText(path);
        Assert.StartsWith(@"{\rtf1", saved);
        Assert.Contains("Times New Roman", saved);
        Assert.Contains(@"negrita\b0", saved);
        Assert.DoesNotContain("Cascadia", saved);
        Assert.DoesNotContain('\0', saved);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));

        // Saved means saved: closing does not ask again.
        app.RequestClose();
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void A_new_rich_document_can_be_formatted_and_asks_before_closing()
    {
        using var app = MemosApp.Launch();

        app.Menu("File", "New rich text document (RTF)");
        MemosApp.Eventually(() => app.TabNames().Length == 2 && app.TryFind("EncodingButton") is null, "the rich document to open in a new tab");

        app.Menu("Format", "Bold");
        app.Type("hola");
        WaitForText(app, "CharactersText", "4 characters");

        app.RequestClose();
        MemosApp.Invoke(app.FindByName("Don't save", ControlType.Button));
        Assert.True(app.WaitForExit(TimeSpan.FromSeconds(15)));
    }

    [Theory]
    [InlineData("nota.txt", "uno\r\ndos")]
    [InlineData("nota.md", "# Título\n\ntexto")]
    [InlineData("nota.rtf", SampleRtf)]
    public void Print_opens_a_preview_window_and_closing_it_leaves_nothing_behind(string name, string content)
    {
        using var app = MemosApp.Launch(docs => [MemosApp.WriteDoc(docs, name, content)]);
        var temp = Path.Combine(app.DataDirectory, "Temp");

        for (var round = 0; round < 2; round++)
        {
            app.Menu("File", "Print…");
            MemosApp.Eventually(() => app.OtherWindows().Length == 1, "the print preview window to open");
            MemosApp.Eventually(() => Directory.GetFiles(temp, "print-*.html").Length == 1, "the page to print to be prepared");
            Thread.Sleep(2500);

            MemosApp.CloseWindow(app.OtherWindows()[0]);
            MemosApp.Eventually(() => app.OtherWindows().Length == 0, "the print preview window to close");
            MemosApp.Eventually(() => Directory.GetFiles(temp, "print-*.html").Length == 0, "the temporary page to be deleted");
        }

        Assert.False(app.HasExited);
        Assert.Equal(content, File.ReadAllText(app.Doc(name)));
    }

    [Fact]
    public void Showing_invisible_characters_and_changing_theme_keep_the_editor_working()
    {
        using var app = MemosApp.Launch(
            docs => [MemosApp.WriteDoc(docs, "espacios.md", "# Título\n\ncon  espacios\ty tab\n")],
            """{ "Language": "en-US", "MarkdownViewMode": "Editor", "ShowInvisibles": true, "SyntaxTheme": "Accent", "Theme": "Dark" }""");

        WaitForText(app, "CharactersText", "27 characters");
        app.Type("a");
        WaitForText(app, "CharactersText", "28 characters");

        app.Menu("View", "Show invisible characters");
        app.Type("b");
        WaitForText(app, "CharactersText", "29 characters");
        Assert.False(app.HasExited);
    }
}
