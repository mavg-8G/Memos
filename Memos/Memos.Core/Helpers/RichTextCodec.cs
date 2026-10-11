using System.Globalization;
using System.Text;

namespace Memos.Core.Helpers;

/// <summary>A stretch of text with the same character styles. Paragraphs end with '\n'.</summary>
public readonly record struct RichTextRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Strikethrough = false);

/// <summary>
/// The RTF that Memos handles without the editing control: creating a document from plain text and
/// reading the text and the four character styles back (for printing, search and statistics).
/// </summary>
public static class RichTextCodec
{
    private readonly record struct State(bool Bold = false, bool Italic = false, bool Underline = false,
        bool Strike = false, bool Skip = false, bool Hidden = false, int UnicodeFallback = 1);

    static RichTextCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool IsRichText(string text) => text.StartsWith(@"{\rtf", StringComparison.Ordinal);

    public static string FromPlainText(string text)
    {
        var rtf = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs22 ");
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': rtf.Append(@"\\"); break;
                case '{': rtf.Append(@"\{"); break;
                case '}': rtf.Append(@"\}"); break;
                case '\n': rtf.Append(@"\par "); break;
                case '\t': rtf.Append(@"\tab "); break;
                default:
                    if (c < 32 || c > 126) rtf.Append(@"\u").Append((short)c).Append('?');
                    else rtf.Append(c);
                    break;
            }
        }
        // Every paragraph is closed, the last one too: a reader takes the final \par as the end of
        // the document, not as an empty line, so text that ends in a line break keeps it.
        return rtf.Append(@"\par}").ToString();
    }

    /// <summary>
    /// An RTF file is bytes, held here one per character. Any character that does not fit in a
    /// byte is written as a \u escape instead of being lost when the file is saved.
    /// </summary>
    public static string EscapeNonLatin(string rtf)
    {
        if (!rtf.AsSpan().ContainsAnyExceptInRange('\0', 'ÿ')) return rtf;

        var escaped = new StringBuilder(rtf.Length + 16);
        foreach (var c in rtf)
        {
            if (c > 'ÿ') escaped.Append(@"\u").Append((short)c).Append('?');
            else escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>The visible text with '\n' between paragraphs, as the editing control reports it.</summary>
    public static string ToPlainText(string rtf)
    {
        var text = string.Concat(Parse(rtf).Select(run => run.Text));

        // The last \par closes the final paragraph; it is not a line break of its own.
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    /// <summary>
    /// Reads the text and the bold, italic, underline and strikethrough styles. Everything else
    /// (fonts, colours, tables, pictures, headers) is skipped; text that is not RTF yields nothing.
    /// </summary>
    public static IReadOnlyList<RichTextRun> Parse(string rtf)
    {
        var runs = new List<RichTextRun>();
        if (!IsRichText(rtf)) return runs;

        var text = new StringBuilder();
        var stack = new Stack<State>();
        var state = new State(UnicodeFallback: 1); // one replacement character unless \uc says otherwise
        var style = new State();
        var fallback = 0;
        var ansi = Encoding.GetEncoding(1252);
        byte? leadByte = null;

        void Flush()
        {
            if (text.Length == 0) return;
            runs.Add(new RichTextRun(text.ToString(), style.Bold, style.Italic, style.Underline, style.Strike));
            text.Clear();
        }

        void Write(char c)
        {
            if (state.Skip || state.Hidden || (c < ' ' && c is not ('\t' or '\n'))) return;
            if (state.Bold != style.Bold || state.Italic != style.Italic || state.Underline != style.Underline || state.Strike != style.Strike)
            {
                Flush();
                style = state;
            }

            text.Append(c);
        }

        // A \uN is followed by a replacement for readers without Unicode, which must be dropped.
        void Append(char c)
        {
            if (fallback > 0) fallback--;
            else Write(c);
        }

        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            if (c == '{') { stack.Push(state); fallback = 0; continue; }
            if (c == '}') { if (stack.Count > 0) state = stack.Pop(); fallback = 0; continue; }
            if (c is '\r' or '\n') continue;
            if (c != '\\') { Append(c); continue; }
            if (++i >= rtf.Length) break;
            c = rtf[i];
            if (c is '\\' or '{' or '}') { Append(c); continue; }
            if (c == '*') { state = state with { Skip = true }; continue; }
            if (c == '\'')
            {
                if (i + 2 >= rtf.Length) break;
                var isHex = byte.TryParse(rtf.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b);
                i += 2;
                if (!isHex) continue;
                if (fallback > 0) { fallback--; continue; }
                if (leadByte is { } lead)
                {
                    leadByte = null;
                    foreach (var decoded in ansi.GetString([lead, b])) Write(decoded);
                }
                else if (!ansi.IsSingleByte && b >= 0x81)
                {
                    leadByte = b; // first half of a double-byte character
                }
                else
                {
                    foreach (var decoded in ansi.GetString([b])) Write(decoded);
                }

                continue;
            }

            if (!char.IsAsciiLetter(c))
            {
                if (c == '~') Append(' ');
                else if (c == '-') Append('­');
                else if (c == '_') Append('‑');
                continue;
            }

            var start = i;
            while (i < rtf.Length && char.IsAsciiLetter(rtf[i])) i++;
            var word = rtf.AsSpan(start, i - start);
            var numberStart = i;
            if (i + 1 < rtf.Length && rtf[i] == '-' && char.IsAsciiDigit(rtf[i + 1])) i++;
            while (i < rtf.Length && char.IsAsciiDigit(rtf[i])) i++;
            var hasNumber = int.TryParse(rtf.AsSpan(numberStart, i - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number);

            // One space after a control word belongs to it; anything else is read again.
            if (i >= rtf.Length || rtf[i] != ' ') i--;
            var on = !hasNumber || number != 0;

            switch (word)
            {
                case "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "object" or "generator"
                    or "header" or "headerl" or "headerr" or "headerf" or "footer" or "footerl" or "footerr" or "footerf"
                    or "footnote" or "listtable" or "listoverridetable" or "revtbl" or "rsidtbl" or "filetbl"
                    or "themedata" or "datastore" or "xmlnstbl":
                    state = state with { Skip = true };
                    break;
                case "ansicpg" when hasNumber:
                    try { ansi = Encoding.GetEncoding(number); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
                    break;
                case "b": state = state with { Bold = on }; break;
                case "i": state = state with { Italic = on }; break;
                case "ulnone": state = state with { Underline = false }; break;
                case "ul" or "uld" or "uldb" or "ulw" or "ulwave" or "ulth" or "uldash" or "uldashd" or "uldashdd":
                    state = state with { Underline = on };
                    break;
                case "strike" or "striked": state = state with { Strike = on }; break;
                case "v": state = state with { Hidden = on }; break;
                case "plain": state = state with { Bold = false, Italic = false, Underline = false, Strike = false, Hidden = false }; break;
                case "uc" when hasNumber: state = state with { UnicodeFallback = Math.Max(0, number) }; break;
                case "u" when hasNumber:
                    Write((char)(ushort)number);
                    fallback = state.UnicodeFallback;
                    break;
                case "bin" when hasNumber && number > 0: i += number; break;
                case "par" or "line" or "sect" or "page" or "row": Append('\n'); break;
                case "tab" or "cell": Append('\t'); break;
                case "emdash": Append('—'); break;
                case "endash": Append('–'); break;
                case "bullet": Append('•'); break;
                case "lquote": Append('‘'); break;
                case "rquote": Append('’'); break;
                case "ldblquote": Append('“'); break;
                case "rdblquote": Append('”'); break;
            }
        }

        Flush();
        return runs;
    }
}
