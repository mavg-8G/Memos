using System.Globalization;
using System.Text;

namespace Memos.Core.Helpers;

/// <summary>Extracts text from session backups written before RTF support was removed.</summary>
internal static class RichTextCodec
{
    static RichTextCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly record struct State(bool Skip = false, bool Hidden = false, int UnicodeFallback = 1);

    public static string ToPlainText(string rtf)
    {
        if (!rtf.StartsWith(@"{\rtf", StringComparison.Ordinal))
        {
            return rtf;
        }

        var text = new StringBuilder();
        var stack = new Stack<State>();
        var state = new State(UnicodeFallback: 1); // one replacement character unless \uc says otherwise
        var fallback = 0;
        var ansi = Encoding.GetEncoding(1252);
        byte? leadByte = null;

        void Write(char c)
        {
            if (state.Skip || state.Hidden || (c < ' ' && c is not ('\t' or '\n'))) return;
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
                case "v": state = state with { Hidden = on }; break;
                case "plain": state = state with { Hidden = false }; break;
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

        // The last \par closes the final paragraph.
        if (text.Length > 0 && text[^1] == '\n') text.Length--;
        return text.ToString();
    }
}
