using System.Globalization;
using System.Text;

namespace Memos.Core.Helpers;

public readonly record struct TextStats(int Characters, int Words)
{
    /// <summary>Estimated reading time at 200 words per minute; zero for an empty document.</summary>
    public int ReadingMinutes => Words == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(Words / 200.0));
}

public static class TextStatistics
{
    /// <summary>
    /// Counts user-perceived characters (grapheme clusters, line breaks excluded) and words.
    /// Scripts without spaces (Han, kana, Thai…) count one word per character, the usual convention.
    /// </summary>
    public static TextStats Compute(string text, CancellationToken cancellationToken = default)
    {
        var span = text.AsSpan();
        var characters = 0;
        var words = 0;
        var inWord = false;
        var sinceCheck = 0;

        while (!span.IsEmpty)
        {
            if (++sinceCheck == 16384)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            var c = span[0];
            if (c < 0x80)
            {
                span = span[1..];
                if (c == '\n')
                {
                    inWord = false;
                    continue;
                }

                // An ASCII char followed by a combining mark is still one grapheme; the mark itself is
                // skipped below because it extends the previous cluster.
                characters++;
                var isWordChar = char.IsAsciiLetterOrDigit(c) || (inWord && c is '\'' or '_');
                if (isWordChar && !inWord)
                {
                    words++;
                }

                inWord = isWordChar;
                continue;
            }

            var length = StringInfo.GetNextTextElementLength(span);
            Rune.DecodeFromUtf16(span, out var rune, out _);
            span = span[length..];

            var category = Rune.GetUnicodeCategory(rune);
            if (IsCombining(category) && characters > 0 && c != '\n')
            {
                // Combining mark right after an ASCII base: part of the previous grapheme.
                continue;
            }

            characters++;
            if (IsSpaceless(rune.Value))
            {
                words++;
                inWord = false;
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                if (!inWord)
                {
                    words++;
                }

                inWord = true;
            }
            else if (!(inWord && rune.Value == 0x2019))
            {
                inWord = false;
            }
        }

        return new TextStats(characters, words);
    }

    private static bool IsCombining(UnicodeCategory category) =>
        category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool IsSpaceless(int cp) =>
        cp is (>= 0x3040 and <= 0x30FF)     // Hiragana, Katakana
            or (>= 0x3400 and <= 0x4DBF)    // CJK extension A
            or (>= 0x4E00 and <= 0x9FFF)    // CJK unified ideographs
            or (>= 0xF900 and <= 0xFAFF)    // CJK compatibility ideographs
            or (>= 0x20000 and <= 0x3FFFF)  // CJK extensions B and later
            or (>= 0x0E00 and <= 0x0E7F);   // Thai
}
