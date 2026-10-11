using System.Text;
using Memos.Core.Helpers;
using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;

namespace Memos.Tests;

public class RichTextTests
{
    /// <summary>What the editing control writes for two paragraphs with fonts, a colour and bold text.</summary>
    private const string RichEditSample =
        "{\\rtf1\\fbidis\\ansi\\ansicpg1252\\deff0\\nouicompat{\\fonttbl{\\f0\\fnil Times New Roman;}{\\f1\\fnil Consolas;}}\r\n" +
        "{\\colortbl ;\\red200\\green0\\blue0;}\r\n" +
        "{\\*\\generator Riched20 3.2.0000}\\viewkind4\\uc1 \r\n" +
        "\\pard\\f0\\fs32\\lang1033 Times grande \\b negrita\\b0\\par\r\n" +
        "\\f1\\fs20 Consolas \\cf1 rojo\\cf0  normal\\par\r\n" +
        "}\r\n";

    private static string Rtf(string body) => @"{\rtf1\ansi " + body + "}";

    // ---- Plain text to RTF ----------------------------------------------------------------------

    [Fact]
    public void Plain_text_conversion_escapes_rtf_and_preserves_unicode()
    {
        var rtf = RichTextCodec.FromPlainText("año {memo}\\\n👍");
        Assert.StartsWith("{\\rtf1", rtf);
        Assert.Contains(@"\u241?", rtf);
        Assert.Contains(@"\{memo\}", rtf);
        Assert.Contains(@"\\", rtf);
        Assert.Contains(@"\par", rtf);
    }

    [Fact]
    public void Converted_text_is_pure_ascii_so_any_reader_can_open_it()
    {
        var rtf = RichTextCodec.FromPlainText("ñ €\t👍\u05e9");
        Assert.All(rtf, c => Assert.InRange(c, ' ', '~'));
        Assert.EndsWith("}", rtf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hola")]
    [InlineData("año {memo} \\ fin")]
    [InlineData("uno\ndos\n\ntres")]
    [InlineData("\tsangrado\tcon\ttabs")]
    [InlineData("emoji 👍 y familia 👨‍👩‍👧")]
    [InlineData("שלום العربية 日本語 €")]
    [InlineData("termina en salto\n")]
    [InlineData("  espacios   varios  ")]
    [InlineData("\\b no es negrita, \\par tampoco es un párrafo")]
    public void Plain_text_survives_the_round_trip_through_rtf(string text) =>
        Assert.Equal(text, RichTextCodec.ToPlainText(RichTextCodec.FromPlainText(text)));

    // ---- RTF to text and styles -----------------------------------------------------------------

    [Fact]
    public void Parser_splits_the_text_into_styled_runs()
    {
        var runs = RichTextCodec.Parse(Rtf(@"plain \b bold \i both\b0  italic\i0  \ul under\ulnone  \strike gone\strike0  end"));

        Assert.Equal(
        [
            new RichTextRun("plain "),
            new RichTextRun("bold ", Bold: true),
            new RichTextRun("both", Bold: true, Italic: true),
            new RichTextRun(" italic", Italic: true),
            new RichTextRun(" "),
            new RichTextRun("under", Underline: true),
            new RichTextRun(" "),
            new RichTextRun("gone", Strikethrough: true),
            new RichTextRun(" end"),
        ], runs);
    }

    [Fact]
    public void Styles_set_inside_a_group_end_with_the_group()
    {
        var runs = RichTextCodec.Parse(Rtf(@"a{\b b{\i c}d}e"));

        Assert.Equal(
        [
            new RichTextRun("a"),
            new RichTextRun("b", Bold: true),
            new RichTextRun("c", Bold: true, Italic: true),
            new RichTextRun("d", Bold: true),
            new RichTextRun("e"),
        ], runs);
    }

    [Fact]
    public void Plain_resets_every_style_and_a_zero_parameter_turns_one_off()
    {
        Assert.Equal([new RichTextRun("a", true, true, true, true), new RichTextRun("b")],
            RichTextCodec.Parse(Rtf(@"\b\i\ul\strike a\plain b")));
        Assert.Equal([new RichTextRun("a", Bold: true), new RichTextRun("b")],
            RichTextCodec.Parse(Rtf(@"\b1 a\b0 b")));
    }

    [Theory]
    [InlineData(@"\uldb")]
    [InlineData(@"\ulw")]
    [InlineData(@"\ulwave")]
    [InlineData(@"\uldash")]
    public void Every_kind_of_underline_counts_as_underline(string control) =>
        Assert.Equal([new RichTextRun("x", Underline: true), new RichTextRun("y")], RichTextCodec.Parse(Rtf(control + @" x\ul0 y")));

    [Fact]
    public void Tables_of_fonts_colours_and_other_destinations_are_not_text()
    {
        var rtf = @"{\rtf1\ansi{\fonttbl{\f0 Segoe UI;}{\f1 Consolas;}}{\colortbl ;\red255\green0\blue0;}" +
                  @"{\stylesheet{\s0 Normal;}}{\info{\title Secreto}{\author Alguien}}{\*\generator Riched20 10.0;}" +
                  @"{\header encabezado}{\footer pie}visible{\pict\wmetafile8 0100090000}{\*\unknown oculto} fin}";

        Assert.Equal("visible fin", RichTextCodec.ToPlainText(rtf));
    }

    [Fact]
    public void Hidden_text_is_left_out()
    {
        Assert.Equal("antes despues", RichTextCodec.ToPlainText(Rtf(@"antes{\v oculto} \v tambien\v0 despues")));
    }

    [Fact]
    public void Hex_escapes_are_read_with_the_code_page_of_the_document()
    {
        Assert.Equal("Café “citado” – 5 €", RichTextCodec.ToPlainText(@"{\rtf1\ansi\ansicpg1252 Caf\'e9 \'93citado\'94 \'96 5 \'80}"));
        Assert.Equal("Привет", RichTextCodec.ToPlainText(@"{\rtf1\ansi\ansicpg1251 \'cf\'f0\'e8\'e2\'e5\'f2}"));
        Assert.Equal("日本", RichTextCodec.ToPlainText(@"{\rtf1\ansi\ansicpg932 \'93\'fa\'96\'7b}"));

        // Windows-1252 is what RTF means when it does not say, and an unknown code page changes nothing.
        Assert.Equal("ñ", RichTextCodec.ToPlainText(@"{\rtf1\ansi \'f1}"));
        Assert.Equal("ñ", RichTextCodec.ToPlainText(@"{\rtf1\ansi\ansicpg99999 \'f1}"));
    }

    [Fact]
    public void Unicode_escapes_drop_their_fallback_and_keep_surrogate_pairs_together()
    {
        Assert.Equal("ñ 👍 fin", RichTextCodec.ToPlainText(Rtf(@"\u241? \u-10179?\u-9139? fin")));
        Assert.Equal("ñfin", RichTextCodec.ToPlainText(Rtf(@"\u241\'f1fin")));
        Assert.Equal("ñfin", RichTextCodec.ToPlainText(Rtf(@"\uc0\u241 fin")));
        Assert.Equal("ñfin", RichTextCodec.ToPlainText(Rtf(@"\uc2\u241 ??fin")));

        // The fallback count belongs to the group that set it and never eats past the group's end.
        Assert.Equal("ñ??", RichTextCodec.ToPlainText(Rtf(@"{\uc0\u241 }\u63 ??")));
        Assert.Equal("ñb", RichTextCodec.ToPlainText(Rtf(@"{\uc2\u241 a}b")));
    }

    [Fact]
    public void Special_characters_and_breaks_become_text()
    {
        Assert.Equal("a\u00a0b\u2011c — – • ‘x’ “y”", RichTextCodec.ToPlainText(Rtf(@"a\~b\_c \emdash  \endash  \bullet  \lquote x\rquote  \ldblquote y\rdblquote ")));
        Assert.Equal("uno\ndos\ntres\tcuatro", RichTextCodec.ToPlainText(Rtf(@"uno\par dos\line tres\tab cuatro\par")));
        Assert.Equal(@"llaves {} y barra \", RichTextCodec.ToPlainText(Rtf(@"llaves \{\} y barra \\")));
        Assert.Equal("a\tb\nc\td", RichTextCodec.ToPlainText(Rtf(@"\trowd a\cell b\row c\cell d\row")));
    }

    [Fact]
    public void Line_breaks_of_the_file_itself_are_not_text_and_a_space_ends_a_control_word()
    {
        Assert.Equal("uno dos", RichTextCodec.ToPlainText("{\\rtf1\\ansi uno\r\n dos\r\n}"));
        Assert.Equal(" x", RichTextCodec.ToPlainText(Rtf(@"\b  x")));
        Assert.Equal("-5", RichTextCodec.ToPlainText(Rtf(@"\b -5")));
    }

    [Fact]
    public void Binary_data_is_skipped_by_its_length()
    {
        Assert.Equal("ab", RichTextCodec.ToPlainText(Rtf(@"a\bin5 }{\x}b")));
    }

    [Fact]
    public void Output_of_the_editing_control_is_understood()
    {
        Assert.Equal("Times grande negrita\nConsolas rojo normal", RichTextCodec.ToPlainText(RichEditSample));
        Assert.Equal(
        [
            new RichTextRun("Times grande "),
            new RichTextRun("negrita", Bold: true),
            new RichTextRun("\nConsolas rojo normal\n"),
        ], RichTextCodec.Parse(RichEditSample));
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto normal, no RTF")]
    [InlineData("{\\rt")]
    [InlineData("\\rtf1 sin llave")]
    public void Text_that_is_not_rtf_yields_nothing(string text)
    {
        Assert.False(RichTextCodec.IsRichText(text));
        Assert.Empty(RichTextCodec.Parse(text));
        Assert.Equal("", RichTextCodec.ToPlainText(text));
    }

    [Fact]
    public void Truncated_or_damaged_rtf_never_throws()
    {
        var sample = RichEditSample + @"{\rtf1 \'e9\u-10179?\bin3 abc\'zz\b-\uc\u}}}}";
        for (var length = 0; length <= sample.Length; length++)
        {
            _ = RichTextCodec.Parse(sample[..length]);
        }

        Assert.Equal("a", RichTextCodec.ToPlainText(@"{\rtf1 a}}}}{{{"));
        Assert.Equal("", RichTextCodec.ToPlainText(@"{\rtf1 \bin999999 x}"));
    }

    [Fact]
    public void Characters_outside_latin1_are_escaped_before_saving()
    {
        const string ascii = @"{\rtf1\ansi Caf\'e9}";
        Assert.Same(ascii, RichTextCodec.EscapeNonLatin(ascii));
        Assert.Equal("{\\rtf1 \u00e9 \\u8364? \\u-10179?\\u-9139?}", RichTextCodec.EscapeNonLatin("{\\rtf1 \u00e9 € 👍}"));
        Assert.Equal("é € 👍", RichTextCodec.ToPlainText(RichTextCodec.EscapeNonLatin("{\\rtf1\\ansi \u00e9 € 👍}")));
    }

    // ---- Files ----------------------------------------------------------------------------------

    [Fact]
    public async Task Rtf_file_round_trips_bytes()
    {
        using var temp = new TempDirectory();
        var file = new FileService();
        var path = temp.File("note.rtf");
        const string rtf = @"{\rtf1\ansi Caf\'e9}";
        await file.SaveRichTextAsync(path, rtf);
        Assert.Equal(rtf, (await file.LoadRichTextAsync(path)).Rtf);
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes(rtf), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Rtf_bytes_above_ascii_are_kept_one_to_one()
    {
        using var temp = new TempDirectory();
        var file = new FileService();
        var path = temp.File("bytes.rtf");
        var bytes = Encoding.ASCII.GetBytes(@"{\rtf1\ansi ").Concat(Enumerable.Range(0x80, 0x80).Select(b => (byte)b)).Append((byte)'}').ToArray();
        File.WriteAllBytes(path, bytes);

        var loaded = await file.LoadRichTextAsync(path);
        Assert.Equal(file.GetStamp(path), loaded.Stamp);
        await file.SaveRichTextAsync(path, loaded.Rtf);

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Saving_rtf_never_loses_characters_that_do_not_fit_in_a_byte()
    {
        using var temp = new TempDirectory();
        var file = new FileService();
        var path = temp.File("unicode.rtf");

        await file.SaveRichTextAsync(path, "{\\rtf1\\ansi precio 5 € 👍}");

        var saved = (await file.LoadRichTextAsync(path)).Rtf;
        Assert.All(File.ReadAllBytes(path), b => Assert.InRange(b, (byte)0x20, (byte)0x7e));
        Assert.Equal("precio 5 € 👍", RichTextCodec.ToPlainText(saved));
    }

    [Fact]
    public async Task A_file_that_is_not_rtf_is_rejected_as_a_file_error()
    {
        using var temp = new TempDirectory();
        var file = new FileService();
        var path = temp.File("falso.rtf");
        File.WriteAllText(path, "solo texto");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => file.LoadRichTextAsync(path));
        Assert.True(FileService.IsFileSystemError(error));

        File.WriteAllBytes(path, []);
        Assert.True(FileService.IsFileSystemError(await Assert.ThrowsAnyAsync<Exception>(() => file.LoadRichTextAsync(path))));
        await Assert.ThrowsAsync<FileNotFoundException>(() => file.LoadRichTextAsync(temp.File("no-existe.rtf")));
    }

    [Fact]
    public async Task Rtf_is_not_written_over_a_read_only_file()
    {
        using var temp = new TempDirectory();
        var file = new FileService();
        var path = temp.File("fijo.rtf");
        File.WriteAllText(path, @"{\rtf1 original}");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        await Assert.ThrowsAsync<ReadOnlyFileException>(() => file.SaveRichTextAsync(path, @"{\rtf1 nuevo}"));
        Assert.Equal(@"{\rtf1 original}", File.ReadAllText(path));
    }

    // ---- Document -------------------------------------------------------------------------------

    private static LoadedRichText Loaded(string rtf) => new(rtf, default);

    [Fact]
    public void Loading_rtf_without_an_editor_exposes_its_text_for_search_and_statistics()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);

        document.LoadRichText(Loaded(Rtf(@"uno \b dos\b0\par tres")), @"C:\docs\nota.rtf");

        Assert.Equal("uno dos\ntres", document.Text);
        Assert.Equal(3, document.WordCount);
        Assert.Equal(2, document.LineCount);
        Assert.Equal("nota.rtf", document.DisplayName);
        Assert.False(document.IsModified);
        Assert.False(document.CanUndo);
        Assert.False(document.IsPristine);
    }

    [Fact]
    public void The_editors_own_way_of_writing_rtf_does_not_make_an_opened_file_look_modified()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);
        var original = Rtf("hola");
        document.LoadRichText(Loaded(original), @"C:\docs\nota.rtf");

        var editor = new FakeRichEditor(document);

        Assert.NotEqual(original, editor.Rtf);
        Assert.Equal(editor.Rtf, document.RichText);
        Assert.Equal("hola", document.Text);
        Assert.False(document.IsModified);

        // The control reports its content again after loading, unchanged.
        var version = document.TextVersion;
        document.OnEditorRichTextChanged("hola", editor.Rtf);
        Assert.False(document.IsModified);
        Assert.Equal(version, document.TextVersion);
    }

    [Fact]
    public void A_restored_unsaved_document_stays_modified_when_the_editor_loads_it()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);
        document.RestoreRichText(Rtf("borrador"), modified: true);
        Assert.True(document.IsModified);
        Assert.Equal("borrador", document.Text);

        var editor = new FakeRichEditor(document);

        Assert.True(document.IsModified);
        Assert.Equal(editor.Rtf, document.RichText);
    }

    [Fact]
    public void A_new_rich_document_is_pristine_until_it_is_edited()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);
        var editor = new FakeRichEditor(document);
        Assert.True(document.IsPristine);
        Assert.False(document.IsModified);

        editor.Edit(Rtf("x"));

        Assert.False(document.IsPristine);
        Assert.True(document.IsModified);
        Assert.Equal("x", document.Text);
    }

    [Fact]
    public void A_change_of_formatting_alone_modifies_the_document_and_is_announced()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);
        document.LoadRichText(Loaded(Rtf("hola")), @"C:\docs\nota.rtf");
        var editor = new FakeRichEditor(document);
        var saved = editor.Rtf;
        var changes = 0;
        document.TextChanged += (_, _) => changes++;
        var version = document.TextVersion;

        editor.Edit(Rtf(@"\b hola"));

        Assert.Equal("hola", document.Text);
        Assert.True(document.IsModified);
        Assert.Equal(1, changes);
        Assert.True(document.TextVersion > version);

        // Back to what is on disk (an undo in the control): nothing left to save.
        document.OnEditorRichTextChanged("hola", saved);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void Undo_and_redo_belong_to_the_editor_and_their_state_follows_it()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);
        var editor = new FakeRichEditor(document);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);

        editor.Edit(Rtf("x"));
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);

        document.Undo();
        Assert.Equal(1, editor.Undos);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);

        // The control reports the undone content afterwards; redo must stay available.
        document.OnEditorRichTextChanged("", FakeRichEditor.Normalize(Rtf("")));
        Assert.True(document.CanRedo);

        document.Redo();
        Assert.Equal(1, editor.Redos);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void Undo_without_an_editor_does_nothing()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);
        document.RestoreRichText(Rtf("x"), modified: true);

        document.Undo();
        document.Redo();
        document.ToggleRichStyle(RichStyle.Bold);

        Assert.Equal("x", document.Text);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void Styles_are_toggled_by_the_editor()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);
        var editor = new FakeRichEditor(document);

        document.ToggleRichStyle(RichStyle.Underline);
        document.ToggleRichStyle(RichStyle.Strikethrough);

        Assert.Equal([RichStyle.Underline, RichStyle.Strikethrough], editor.Toggled);
    }

    [Fact]
    public void Saving_makes_the_current_rtf_the_reference()
    {
        var document = TestHelpers.NewDocument(DocumentKind.RichText);
        var editor = new FakeRichEditor(document);
        editor.Edit(Rtf("x"));

        document.MarkSaved(@"C:\docs\x.rtf", default);
        Assert.False(document.IsModified);

        editor.Edit(Rtf("xy"));
        Assert.True(document.IsModified);
    }

    [Fact]
    public void Converting_text_keeps_the_content_and_detaches_the_original_file()
    {
        var document = TestHelpers.NewDocument();
        document.LoadFrom(new LoadedText("año {1}\n👍", TextEncodingId.Utf8, LineEnding.Lf, false, false, false, default), @"C:\docs\notas.txt");

        document.ConvertToRichText();

        Assert.True(document.IsRichText);
        Assert.Equal(DocumentKind.RichText, document.Kind);
        Assert.Null(document.FilePath);
        Assert.Null(document.Stamp);
        Assert.Equal("notas", document.DisplayName);
        Assert.True(document.IsModified);
        Assert.Equal("año {1}\n👍", document.Text);
        Assert.Equal("año {1}\n👍", RichTextCodec.ToPlainText(document.RichText));

        var rtf = document.RichText;
        document.ConvertToRichText();
        Assert.Same(rtf, document.RichText);
    }

    [Fact]
    public void Converting_an_untouched_blank_tab_leaves_it_untouched()
    {
        var document = TestHelpers.NewDocument();

        document.ConvertToRichText();

        Assert.True(document.IsRichText);
        Assert.False(document.IsModified);
        Assert.True(document.IsPristine);
    }

    // ---- Application ----------------------------------------------------------------------------

    private static string WriteRtf(TestApp app, string name, string rtf)
    {
        var path = app.Temp.File(name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(rtf));
        return path;
    }

    [Theory]
    [InlineData("a.rtf", DocumentKind.RichText)]
    [InlineData("A.RTF", DocumentKind.RichText)]
    [InlineData("a.md", DocumentKind.Markdown)]
    [InlineData("a.rtf.txt", DocumentKind.Text)]
    [InlineData("rtf", DocumentKind.Text)]
    public void The_rtf_extension_selects_the_rich_text_workspace(string name, DocumentKind kind) =>
        Assert.Equal(kind, MainViewModel.GetKindForPath(name));

    [Fact]
    public async Task Opening_an_rtf_file_gives_an_unmodified_rich_tab_with_its_text()
    {
        using var app = new TestApp();
        var path = WriteRtf(app, "carta.rtf", @"{\rtf1\ansi\ansicpg1252 Estimada \b Ana\b0 :\par Caf\'e9}");

        await app.Main.InitializeAsync([path]);

        var document = Assert.Single(app.Main.Documents);
        Assert.True(document.IsRichText);
        Assert.True(app.Main.IsRichTextActive);
        Assert.False(app.Main.IsMarkdownActive);
        Assert.Equal("Estimada Ana:\nCafé", document.Text);
        Assert.False(document.IsModified);
        Assert.Equal(path, document.FilePath);
        Assert.Contains(path, app.Recent.Items);
        Assert.Empty(app.Dialogs.Messages);
    }

    [Fact]
    public async Task A_file_named_rtf_that_is_not_rtf_reports_an_error_and_opens_nothing()
    {
        using var app = new TestApp();
        var path = app.WriteFile("falso.rtf", "esto es texto normal");

        await app.Main.InitializeAsync([path]);

        Assert.Single(app.Dialogs.Messages);
        Assert.StartsWith("Msg_ReadError", app.Dialogs.Messages[0]);
        Assert.True(Assert.Single(app.Main.Documents).IsPristine);
    }

    [Fact]
    public async Task Formatting_commands_follow_the_kind_of_document()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        Assert.False(app.Main.BoldCommand.CanExecute(null));
        Assert.False(app.Main.UnderlineCommand.CanExecute(null));
        Assert.True(app.Main.ZoomInCommand.CanExecute(null));

        app.Main.NewRichTextCommand.Execute(null);
        var editor = new FakeRichEditor(app.Active);

        Assert.True(app.Main.IsRichTextActive);
        Assert.True(app.Main.BoldCommand.CanExecute(null));
        Assert.True(app.Main.UnderlineCommand.CanExecute(null));
        Assert.False(app.Main.InlineCodeCommand.CanExecute(null));
        Assert.False(app.Main.ExportPdfCommand.CanExecute(null));
        Assert.False(app.Main.ZoomInCommand.CanExecute(null));

        app.Main.BoldCommand.Execute(null);
        app.Main.ItalicCommand.Execute(null);
        app.Main.UnderlineCommand.Execute(null);
        app.Main.StrikethroughCommand.Execute(null);

        Assert.Equal([RichStyle.Bold, RichStyle.Italic, RichStyle.Underline, RichStyle.Strikethrough], editor.Toggled);
        Assert.Equal("", app.Active.Text);

        app.Main.NewMarkdownCommand.Execute(null);
        Assert.False(app.Main.IsRichTextActive);
        Assert.True(app.Main.BoldCommand.CanExecute(null));
        Assert.False(app.Main.UnderlineCommand.CanExecute(null));
        app.Main.BoldCommand.Execute(null);
        Assert.Contains("**", app.Active.Text);
    }

    [Fact]
    public async Task Saving_a_new_rich_document_writes_rtf_and_reopens_identically()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Main.NewRichTextCommand.Execute(null);
        var editor = new FakeRichEditor(app.Active);
        editor.Edit(Rtf(@"hola \b mundo\b0  \u8364?"));
        var path = app.Temp.File("nuevo.rtf");
        app.Pickers.SaveAnswer = path;

        await app.Main.SaveCommand.ExecuteAsync(null);

        Assert.False(app.Active.IsModified);
        Assert.Equal("nuevo.rtf", app.Active.DisplayName);
        Assert.Equal(Encoding.Latin1.GetBytes(editor.Rtf), File.ReadAllBytes(path));

        using var second = new TestApp();
        await second.Main.InitializeAsync([path]);
        Assert.Equal("hola mundo €", second.Active.Text);
        Assert.Equal(editor.Rtf, second.Active.RichText);
    }

    [Fact]
    public async Task A_rich_document_cannot_be_saved_under_another_extension()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Main.NewRichTextCommand.Execute(null);
        new FakeRichEditor(app.Active).Edit(Rtf("x"));
        var path = app.Temp.File("nota.txt");
        app.Pickers.SaveAnswer = path;

        Assert.False(await app.Main.SaveDocumentAsync(app.Active, saveAs: false));

        Assert.Equal(["Msg_RtfExtension"], app.Dialogs.Messages);
        Assert.False(File.Exists(path));
        Assert.True(app.Active.IsModified);
        Assert.Null(app.Active.FilePath);
    }

    [Fact]
    public async Task Saving_text_as_rtf_converts_it_and_leaves_the_original_file_alone()
    {
        using var app = new TestApp();
        var original = app.WriteFile("notas.txt", "línea 1\nlínea {2}");
        await app.Main.InitializeAsync([original]);
        var target = app.Temp.File("notas.rtf");
        app.Pickers.SaveAnswer = target;

        await app.Main.SaveAsCommand.ExecuteAsync(null);

        Assert.True(app.Active.IsRichText);
        Assert.True(app.Main.IsRichTextActive);
        Assert.Equal(target, app.Active.FilePath);
        Assert.False(app.Active.IsModified);
        Assert.Equal("línea 1\nlínea {2}", File.ReadAllText(original));
        var saved = File.ReadAllText(target, Encoding.Latin1);
        Assert.StartsWith(@"{\rtf1", saved);
        Assert.Equal("línea 1\nlínea {2}", RichTextCodec.ToPlainText(saved));
    }

    [Fact]
    public async Task Turning_formatting_on_and_off_again_asks_nothing_and_keeps_the_text()
    {
        using var app = new TestApp();
        var path = app.WriteFile("lista.txt", "uno\ndos");
        await app.Main.InitializeAsync([path]);

        await app.Main.ToggleFormattingCommand.ExecuteAsync(null);

        Assert.True(app.Active.IsRichText);
        Assert.Null(app.Active.FilePath);
        Assert.Equal("lista", app.Active.DisplayName);
        Assert.True(app.Active.IsModified);
        Assert.Equal("uno\ndos", app.Active.Text);

        // The control rewrites the RTF its own way; that alone is not formatting.
        _ = new FakeRichEditor(app.Active);
        Assert.False(app.Active.HasRichFormatting);

        await app.Main.ToggleFormattingCommand.ExecuteAsync(null);

        Assert.Empty(app.Dialogs.Confirmations);
        Assert.Equal(DocumentKind.Text, app.Active.Kind);
        Assert.False(app.Main.IsRichTextActive);
        Assert.Equal("uno\ndos", app.Active.Text);
        Assert.Equal("", app.Active.RichText);
        Assert.Equal("lista", app.Active.DisplayName);
        Assert.Null(app.Active.FilePath);
        Assert.True(app.Active.IsModified);
        Assert.False(app.Active.CanUndo);
        Assert.Equal("uno\ndos", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(DocumentKind.Text)]
    [InlineData(DocumentKind.Markdown)]
    public async Task Leaving_rich_text_asks_before_dropping_the_formatting(DocumentKind target)
    {
        using var app = new TestApp();
        var path = WriteRtf(app, "carta.v2.rtf", Rtf(@"hola \b mundo"));
        await app.Main.InitializeAsync([path]);
        _ = new FakeRichEditor(app.Active);
        var original = File.ReadAllBytes(path);
        Assert.True(app.Active.HasRichFormatting);

        app.Dialogs.ConfirmAnswer = false;
        await app.Main.SetKindCommand.ExecuteAsync(target);

        Assert.Equal(["Msg_DropFormatting"], app.Dialogs.Confirmations);
        Assert.True(app.Active.IsRichText);
        Assert.Equal(path, app.Active.FilePath);
        Assert.False(app.Active.IsModified);

        app.Dialogs.ConfirmAnswer = true;
        await app.Main.SetKindCommand.ExecuteAsync(target);

        Assert.Equal(target, app.Active.Kind);
        Assert.Equal(target == DocumentKind.Markdown, app.Main.IsMarkdownActive);
        Assert.False(app.Main.IsRichTextActive);
        Assert.Equal("hola mundo", app.Active.Text);
        Assert.Null(app.Active.FilePath);
        Assert.Equal("carta.v2", app.Active.DisplayName);
        Assert.True(app.Active.IsModified);
        Assert.Equal(original, File.ReadAllBytes(path));

        // It is an ordinary text document again: it edits, undoes and saves as one.
        app.Active.Editor = null;
        app.Type("¡");
        Assert.Equal("¡hola mundo", app.Active.Text);
        Assert.True(app.Active.CanUndo);
        var saved = app.Temp.File(target == DocumentKind.Markdown ? "carta.md" : "carta.txt");
        app.Pickers.SaveAnswer = saved;
        await app.Main.SaveCommand.ExecuteAsync(null);
        Assert.Equal("¡hola mundo", File.ReadAllText(saved));
        Assert.False(app.Active.IsModified);
        Assert.Equal(target, app.Active.Kind);
    }

    [Fact]
    public async Task A_blank_rich_document_goes_back_to_text_untouched()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Main.NewRichTextCommand.Execute(null);
        _ = new FakeRichEditor(app.Active);

        await app.Main.ToggleFormattingCommand.ExecuteAsync(null);

        Assert.Empty(app.Dialogs.Confirmations);
        Assert.Equal(DocumentKind.Text, app.Active.Kind);
        Assert.True(app.Active.IsPristine);
    }

    [Fact]
    public async Task The_formatting_switch_belongs_to_the_text_workspace()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        Assert.True(app.Main.ToggleFormattingCommand.CanExecute(null));

        app.Main.NewMarkdownCommand.Execute(null);
        Assert.False(app.Main.ToggleFormattingCommand.CanExecute(null));

        app.Main.NewRichTextCommand.Execute(null);
        Assert.True(app.Main.ToggleFormattingCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_converted_document_is_restored_by_the_session_in_its_new_format()
    {
        using var temp = new TempDirectory();
        using (var first = new TestApp(temp))
        {
            first.Settings.RestoreSession = true;
            await first.Main.InitializeAsync([]);
            first.Main.NewRichTextCommand.Execute(null);
            new FakeRichEditor(first.Active).Edit(Rtf(@"\b nota"));
            await first.Main.SaveSessionAsync();

            first.Dialogs.ConfirmAnswer = true;
            await first.Main.SetKindCommand.ExecuteAsync(DocumentKind.Text);
            first.Active.Editor = null;
            first.Main.NewTextCommand.Execute(null);
            first.Type("apunte");
            await first.Main.SaveSessionAsync();
            await first.Main.SetKindCommand.ExecuteAsync(DocumentKind.RichText);

            Assert.True(await first.Main.PrepareToCloseAsync());
            await first.SettingsService.FlushAsync();
        }

        using var second = new TestApp(temp);
        await second.Main.InitializeAsync([]);

        var restored = second.Main.Documents.Where(d => !d.IsPristine).ToList();
        Assert.Equal([DocumentKind.Text, DocumentKind.RichText], restored.Select(d => d.Kind));
        Assert.Equal(["nota", "apunte"], restored.Select(d => d.Text));
    }

    [Fact]
    public async Task Closing_a_modified_rich_document_asks_and_reopening_brings_its_formatting_back()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([]);
        app.Main.NewRichTextCommand.Execute(null);
        var editor = new FakeRichEditor(app.Active);
        editor.Edit(Rtf(@"\b importante"));
        var rtf = editor.Rtf;
        app.Dialogs.SaveChangesAnswer = SaveChangesChoice.Discard;

        await app.Main.CloseTabCommand.ExecuteAsync(null);
        Assert.Equal(1, app.Dialogs.SaveChangesPrompts);
        await app.Main.ReopenClosedCommand.ExecuteAsync(null);

        Assert.True(app.Active.IsRichText);
        Assert.Equal(rtf, app.Active.RichText);
        Assert.Equal("importante", app.Active.Text);
        Assert.True(app.Active.IsModified);
    }

    [Fact]
    public async Task Session_restores_rich_documents_saved_and_unsaved()
    {
        using var temp = new TempDirectory();
        string saved, draft;
        using (var first = new TestApp(temp))
        {
            first.Settings.RestoreSession = true;
            saved = WriteRtf(first, "guardado.rtf", Rtf(@"en \i disco"));
            await first.Main.InitializeAsync([saved]);
            first.Main.NewRichTextCommand.Execute(null);
            var editor = new FakeRichEditor(first.Active);
            editor.Edit(Rtf(@"borrador \b con formato\b0  \u241?"));
            draft = editor.Rtf;

            Assert.True(await first.Main.PrepareToCloseAsync());
            Assert.Equal(0, first.Dialogs.SaveChangesPrompts);
            await first.SettingsService.FlushAsync();
        }

        using var second = new TestApp(temp);
        await second.Main.InitializeAsync([]);

        Assert.Equal([DocumentKind.RichText, DocumentKind.RichText], second.Main.Documents.Select(d => d.Kind));
        Assert.Equal([false, true], second.Main.Documents.Select(d => d.IsModified));
        Assert.Equal("en disco", second.Main.Documents[0].Text);
        Assert.Equal(draft, second.Main.Documents[1].RichText);
        Assert.Equal("borrador con formato ñ", second.Main.Documents[1].Text);
        Assert.Same(second.Main.Documents[1], second.Active);
        Assert.True(second.Main.IsRichTextActive);
    }

    [Fact]
    public async Task A_session_file_that_stopped_being_rtf_does_not_leave_an_empty_tab()
    {
        using var temp = new TempDirectory();
        using (var first = new TestApp(temp))
        {
            first.Settings.RestoreSession = true;
            var path = WriteRtf(first, "cambia.rtf", Rtf("x"));
            await first.Main.InitializeAsync([path]);
            Assert.True(await first.Main.PrepareToCloseAsync());
            await first.SettingsService.FlushAsync();
            File.WriteAllText(path, "ya no es rtf");
        }

        using var second = new TestApp(temp);
        await second.Main.InitializeAsync([]);

        Assert.True(Assert.Single(second.Main.Documents).IsPristine);
    }

    [Fact]
    public async Task Rich_text_in_any_tab_is_found_by_the_search_across_tabs()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([WriteRtf(app, "a.rtf", Rtf(@"la \b aguja\b0  en el pajar")), app.WriteFile("b.txt", "otra aguja")]);

        Assert.Equal(["la aguja en el pajar", "otra aguja"], app.Main.Documents.Select(d => d.Text));
        Assert.All(app.Main.Documents, d => Assert.Contains("aguja", d.Text));
    }

    // ---- Printing -------------------------------------------------------------------------------

    [Fact]
    public async Task Printing_plain_text_sends_the_text_itself_in_the_editor_font()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([app.WriteFile("código.txt", "if (a < b && c > d) {\n\t\"x\"\n}")]);

        await app.Main.PrintCommand.ExecuteAsync(null);

        Assert.Equal("código.txt", app.Print.Title);
        Assert.Equal([new RichTextRun("if (a < b && c > d) {\n\t\"x\"\n}")], app.Print.Runs);
        Assert.True(app.Print.Monospaced);
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
        Assert.Equal([new PrintSpan("Título")], app.Print.Blocks[0].Spans);
        Assert.Equal([new PrintSpan("texto "), new PrintSpan("fuerte", Bold: true)], app.Print.Blocks[1].Spans);
        Assert.Null(app.Print.Runs);
    }

    [Fact]
    public async Task Printing_rich_text_sends_its_styles_and_not_its_rtf()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([WriteRtf(app, "carta.rtf", RichEditSample)]);

        await app.Main.PrintCommand.ExecuteAsync(null);

        Assert.Equal("carta.rtf", app.Print.Title);
        Assert.False(app.Print.Monospaced);
        Assert.Equal(
            [new RichTextRun("Times grande "), new RichTextRun("negrita", Bold: true), new RichTextRun("\nConsolas rojo normal\n")],
            app.Print.Runs);
    }

    [Fact]
    public async Task Printing_uses_what_is_on_screen_not_what_is_on_disk()
    {
        using var app = new TestApp();
        await app.Main.InitializeAsync([WriteRtf(app, "nota.rtf", Rtf("viejo"))]);
        new FakeRichEditor(app.Active).Edit(Rtf(@"\ul nuevo"));

        await app.Main.PrintCommand.ExecuteAsync(null);

        Assert.Equal([new RichTextRun("nuevo", Underline: true)], app.Print.Runs);
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
        app.Main.NewRichTextCommand.Execute(null);
        Assert.True(app.Main.PrintCommand.CanExecute(null));

        // A blank rich document prints as a blank page instead of failing.
        await app.Main.PrintCommand.ExecuteAsync(null);
        Assert.Empty(app.Print.Runs!);
    }
}
