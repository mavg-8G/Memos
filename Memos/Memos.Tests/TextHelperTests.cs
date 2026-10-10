using Memos.Core.Helpers;
using Memos.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace Memos.Tests;

public class TextOperationsTests
{
    private static (string Text, int Start, int Length) Apply(string text, TextEdit? edit)
    {
        Assert.NotNull(edit);
        return (edit.Value.Apply(text), edit.Value.SelectionStart, edit.Value.SelectionLength);
    }

    [Fact]
    public void Duplicate_line_copies_the_caret_line_below_and_follows_it()
    {
        var (text, start, _) = Apply("uno\ndos\ntres", TextOperations.DuplicateLines("uno\ndos\ntres", 5, 0));

        Assert.Equal("uno\ndos\ndos\ntres", text);
        Assert.Equal(9, start);
    }

    [Fact]
    public void Duplicate_works_on_every_selected_line()
    {
        var (text, _, _) = Apply("a\nb\nc", TextOperations.DuplicateLines("a\nb\nc", 0, 3));

        Assert.Equal("a\nb\na\nb\nc", text);
    }

    [Theory]
    [InlineData("uno\ndos\ntres", 5, "uno\ntres")]
    [InlineData("uno\ndos\ntres", 9, "uno\ndos")]
    [InlineData("solo", 2, "")]
    public void Delete_line_removes_the_line_and_its_break(string source, int caret, string expected)
    {
        var (text, _, _) = Apply(source, TextOperations.DeleteLines(source, caret, 0));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Move_lines_swaps_with_the_neighbour_and_stops_at_the_edges()
    {
        const string source = "uno\ndos\ntres";

        Assert.Equal("dos\nuno\ntres", Apply(source, TextOperations.MoveLines(source, 5, 0, up: true)).Text);
        Assert.Equal("uno\ntres\ndos", Apply(source, TextOperations.MoveLines(source, 5, 0, up: false)).Text);
        Assert.Null(TextOperations.MoveLines(source, 1, 0, up: true));
        Assert.Null(TextOperations.MoveLines(source, 10, 0, up: false));
    }

    [Fact]
    public void Moving_a_line_keeps_the_caret_on_it()
    {
        var edit = TextOperations.MoveLines("uno\ndos\ntres", 5, 0, up: false)!.Value;

        Assert.Equal("d|os", edit.Apply("uno\ndos\ntres").Insert(edit.SelectionStart, "|").Split('\n')[2]);
    }

    [Fact]
    public void Case_conversion_handles_accents_and_leaves_other_scripts_alone()
    {
        const string source = "Canción ñandú Ελληνικά 日本語";

        Assert.Equal("CANCIÓN ÑANDÚ ΕΛΛΗΝΙΚΆ 日本語", Apply(source, TextOperations.ChangeCase(source, 0, source.Length, upper: true)).Text);
        Assert.Null(TextOperations.ChangeCase(source, 3, 0, upper: true));
    }

    [Fact]
    public void Trim_trailing_whitespace_only_touches_line_ends()
    {
        const string source = "  uno  \n\tdos\t\n\ntres";

        Assert.Equal("  uno\n\tdos\n\ntres", Apply(source, TextOperations.TrimTrailingWhitespace(source, 0, 0)).Text);
        Assert.Null(TextOperations.TrimTrailingWhitespace("limpio\nya", 0, 0));
    }

    [Fact]
    public void Indent_and_outdent_are_inverse_on_a_block()
    {
        const string source = "uno\ndos\n\ntres";
        var indented = Apply(source, TextOperations.Indent(source, 0, source.Length, "    "));

        Assert.Equal("    uno\n    dos\n\n    tres", indented.Text);

        var outdented = Apply(indented.Text, TextOperations.Outdent(indented.Text, indented.Start, indented.Length, 4));
        Assert.Equal(source, outdented.Text);
        Assert.Null(TextOperations.Outdent(source, 0, source.Length, 4));
    }

    [Fact]
    public void Indentation_converts_between_tabs_and_spaces()
    {
        Assert.Equal("    a\n        b\n  c", Apply("\ta\n\t\tb\n  c", TextOperations.ConvertIndentation("\ta\n\t\tb\n  c", toSpaces: true, 4)).Text);
        Assert.Equal("\ta\n\t\t  b", Apply("    a\n          b", TextOperations.ConvertIndentation("    a\n          b", toSpaces: false, 4)).Text);
    }

    [Fact]
    public void Minimize_reduces_an_edit_to_the_part_that_differs()
    {
        var edit = TextOperations.Minimize("hola mundo cruel", new TextEdit(0, 16, "hola MUNDO cruel", 0, 0));

        Assert.Equal((5, 5, "MUNDO"), (edit.Start, edit.Length, edit.NewText));
    }
}

public class TextDiffTests
{
    [Theory]
    [InlineData("hola", "hola", 4, 0, 0)]
    [InlineData("hola", "hoyla", 2, 0, 1)]
    [InlineData("hola mundo", "hola", 4, 6, 0)]
    [InlineData("", "abc", 0, 0, 3)]
    [InlineData("aaa", "aa", 2, 1, 0)]
    public void Computes_the_smallest_replacement(string a, string b, int start, int removed, int inserted)
    {
        Assert.Equal(new TextChange(start, removed, inserted), TextDiff.Compute(a, b));
    }

    [Fact]
    public void Never_splits_a_surrogate_pair()
    {
        // 😀 and 😁 share their high surrogate; the change must cover the whole character.
        var change = TextDiff.Compute("a😀b", "a😁b");

        Assert.Equal(new TextChange(1, 2, 2), change);
    }
}

public class LineIndexTests
{
    [Fact]
    public void Maps_offsets_to_lines_including_a_trailing_empty_line()
    {
        var index = new LineIndex("uno\ndos\n");

        Assert.Equal(3, index.LineCount);
        Assert.Equal(0, index.GetLineFromOffset(3));
        Assert.Equal(1, index.GetLineFromOffset(4));
        Assert.Equal(2, index.GetLineFromOffset(8));
        Assert.Equal(4, index.GetLineStart(1));
        Assert.Equal(7, index.GetLineEnd(1));
    }

    [Fact]
    public void Column_counts_what_the_user_sees_not_utf16_units()
    {
        const string line = "👨‍👩‍👧‍👦éx";

        Assert.Equal(1, LineIndex.GetColumn(line, 0, 0));
        Assert.Equal(4, LineIndex.GetColumn(line, 0, line.Length)); // family + é + x
    }
}

public class TextStatisticsTests
{
    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("Hola, mundo", 11, 2)]
    [InlineData("it's a well-known fact", 22, 5)]
    [InlineData("uno\ndos\r", 7, 2)]
    [InlineData("👨‍👩‍👧‍👦 🇪🇸 👍🏽", 5, 0)]
    [InlineData("é ñ", 3, 2)]
    [InlineData("日本語", 3, 3)]
    [InlineData("Привет мир שלום עולם مرحبا", 26, 5)]
    public void Counts_graphemes_and_words(string text, int characters, int words)
    {
        var stats = TextStatistics.Compute(text);

        Assert.Equal(characters, stats.Characters);
        Assert.Equal(words, stats.Words);
    }

    [Fact]
    public void Reading_time_rounds_up_from_200_words_per_minute()
    {
        Assert.Equal(0, new TextStats(0, 0).ReadingMinutes);
        Assert.Equal(1, new TextStats(10, 3).ReadingMinutes);
        Assert.Equal(3, new TextStats(3000, 401).ReadingMinutes);
    }
}

public class UndoHistoryTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Typing_merges_into_words_and_undo_restores_step_by_step()
    {
        var history = new UndoHistory(_time);
        var text = "";
        foreach (var c in "hola mundo")
        {
            history.Record(text.Length, "", c.ToString());
            text += c;
        }

        text = history.Undo()!.Value.Apply(text);
        Assert.Equal("hola ", text);
        text = history.Undo()!.Value.Apply(text);
        Assert.Equal("", text);
        Assert.False(history.CanUndo);

        text = history.Redo()!.Value.Apply(text);
        text = history.Redo()!.Value.Apply(text);
        Assert.Equal("hola mundo", text);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void A_pause_starts_a_new_step()
    {
        var history = new UndoHistory(_time);
        history.Record(0, "", "a");
        _time.Advance(TimeSpan.FromSeconds(2));
        history.Record(1, "", "b");

        Assert.Equal("a", history.Undo()!.Value.Apply("ab"));
    }

    [Fact]
    public void Backspaces_merge_and_undo_brings_the_text_back()
    {
        var history = new UndoHistory(_time);
        history.Record(4, "o", "");
        history.Record(3, "d", "");
        history.Record(2, "n", "");

        Assert.Equal("mundo", history.Undo()!.Value.Apply("mu"));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void A_new_edit_discards_the_redo_stack()
    {
        var history = new UndoHistory(_time);
        history.Record(0, "", "a");
        history.Undo();
        history.Record(0, "", "b");

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Saved_state_is_tracked_through_undo_and_redo()
    {
        var history = new UndoHistory(_time);
        Assert.True(history.IsAtSavedState);

        history.Record(0, "", "a");
        Assert.False(history.IsAtSavedState);

        history.MarkSaved();
        history.Record(1, "", "b"); // must not merge into the saved step
        Assert.False(history.IsAtSavedState);

        history.Undo();
        Assert.True(history.IsAtSavedState);
        history.Undo();
        Assert.False(history.IsAtSavedState);
    }
}
