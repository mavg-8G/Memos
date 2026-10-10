using Memos.Core.Models;
using Memos.Core.Services;

namespace Memos.Tests;

public class SearchServiceTests
{
    private readonly SearchService _search = new();

    private int[] Starts(string text, string query, SearchOptions? options = null) =>
        _search.FindAll(text, query, options ?? new SearchOptions()).Select(m => m.Start).ToArray();

    [Fact]
    public void Plain_search_ignores_case_by_default_and_treats_the_query_literally()
    {
        Assert.Equal([0, 5, 10], Starts("Hola hola HOLA", "hola"));
        Assert.Equal([5], Starts("Hola hola HOLA", "hola", new SearchOptions(MatchCase: true)));
        Assert.Equal([1], Starts("a+b (a.b)* a.b", "+b"));
        Assert.Equal([5], Starts("a+b (a.b)* a.b", "a.b)*"));
    }

    [Fact]
    public void Case_insensitive_search_works_beyond_ascii()
    {
        Assert.Equal([0, 8], Starts("CANCIÓN canción", "canción"));
        Assert.Equal([0, 7], Starts("ПРИВЕТ привет", "привет"));
        Assert.Equal([0, 9], Starts("ΕΛΛΗΝΙΚΑ ελληνικα", "ελληνικα"));
    }

    [Fact]
    public void Whole_word_understands_unicode_letters()
    {
        var options = new SearchOptions(WholeWord: true);

        Assert.Equal([0], Starts("año años añoso", "año", options));
        Assert.Equal([4], Starts("мир мирный", "мирный", options));
        Assert.Empty(Starts("sub_total", "total", options));
    }

    [Fact]
    public void Emoji_and_right_to_left_text_can_be_found()
    {
        Assert.Equal([5], Starts("hola 👨‍👩‍👧‍👦 adiós 👍", "👨‍👩‍👧‍👦"));
        Assert.Equal([5], Starts("שלום עולם", "עולם"));
        Assert.Equal([0, 14], Starts("مرحبا بالعالم مرحبا", "مرحبا"));
    }

    [Fact]
    public void Regex_matches_per_line_and_skips_empty_matches()
    {
        var regex = new SearchOptions(UseRegex: true);

        Assert.Equal([0, 4, 8], Starts("uno\ndos\ntres", "^\\w+", regex));
        Assert.Equal([2, 6], Starts("uno\ndos\ntres", "\\w$", regex).Take(2).ToArray());
        Assert.Empty(Starts("abc", "x*", regex));
    }

    [Fact]
    public void Invalid_regex_is_reported_instead_of_crashing()
    {
        var ex = Assert.Throws<SearchQueryException>(() => _search.FindAll("abc", "(", new SearchOptions(UseRegex: true)));

        Assert.False(ex.TimedOut);
    }

    [Fact]
    public void Match_limit_and_cancellation_are_honoured()
    {
        var text = new string('a', 1000);

        Assert.Equal(10, _search.FindAll(text, "a", new SearchOptions(), default, maxMatches: 10).Count);
        Assert.Throws<OperationCanceledException>(() => _search.FindAll(text, "a", new SearchOptions(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Replace_one_is_literal_in_plain_mode_and_expands_groups_in_regex_mode()
    {
        const string text = "precio: 10 EUR";

        var literal = _search.ReplaceOne(text, new TextMatch(8, 2), "10", "$1", new SearchOptions())!.Value;
        var regex = _search.ReplaceOne(text, new TextMatch(8, 6), @"(\d+) (\w+)", "$2 $1", new SearchOptions(UseRegex: true))!.Value;

        Assert.Equal("precio: $1 EUR", literal.Apply(text));
        Assert.Equal("precio: EUR 10", regex.Apply(text));
    }

    [Fact]
    public void Replace_one_does_nothing_if_the_text_no_longer_matches()
    {
        Assert.Null(_search.ReplaceOne("hola mundo", new TextMatch(0, 4), "adiós", "x", new SearchOptions()));
    }

    [Fact]
    public void Replace_all_counts_replacements_and_supports_escapes_in_regex_mode()
    {
        var (plain, plainCount) = _search.ReplaceAll("a-b-c", "-", "+", new SearchOptions());
        var (lines, lineCount) = _search.ReplaceAll("a;b;c", ";", "\\n", new SearchOptions(UseRegex: true));
        var (same, noneCount) = _search.ReplaceAll("abc", "z", "y", new SearchOptions());

        Assert.Equal(("a+b+c", 2), (plain, plainCount));
        Assert.Equal(("a\nb\nc", 2), (lines, lineCount));
        Assert.Equal(("abc", 0), (same, noneCount));
    }

    [Fact]
    public void Replace_all_keeps_supplementary_characters_intact()
    {
        var (text, count) = _search.ReplaceAll("😀 y 😀", "😀", "𝄞", new SearchOptions());

        Assert.Equal(("𝄞 y 𝄞", 2), (text, count));
    }
}
