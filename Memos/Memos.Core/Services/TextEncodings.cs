using Memos.Core.Models;
using System.Text;

namespace Memos.Core.Services;

/// <summary>Detection, decoding and encoding for the encodings the editor supports.</summary>
public static class TextEncodings
{
    public static readonly IReadOnlyList<TextEncodingId> All =
    [
        TextEncodingId.Utf8, TextEncodingId.Utf8Bom, TextEncodingId.Utf16Le, TextEncodingId.Utf16Be, TextEncodingId.Windows1252,
    ];

    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string GetDisplayName(TextEncodingId id) => id switch
    {
        TextEncodingId.Utf8 => "UTF-8",
        TextEncodingId.Utf8Bom => "UTF-8 BOM",
        TextEncodingId.Utf16Le => "UTF-16 LE",
        TextEncodingId.Utf16Be => "UTF-16 BE",
        TextEncodingId.Windows1252 => "Windows-1252",
        _ => id.ToString(),
    };

    /// <summary>
    /// Decides how to read a file: a BOM wins; without one, valid UTF-8 is UTF-8, text full of NUL
    /// bytes in alternating positions is BOM-less UTF-16, and anything else is read as Windows-1252.
    /// </summary>
    public static (TextEncodingId Encoding, bool Guessed) Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]))
        {
            return (TextEncodingId.Utf8Bom, false);
        }

        if (bytes.StartsWith<byte>([0xFF, 0xFE]))
        {
            return (TextEncodingId.Utf16Le, false);
        }

        if (bytes.StartsWith<byte>([0xFE, 0xFF]))
        {
            return (TextEncodingId.Utf16Be, false);
        }

        if (bytes.Contains((byte)0) && GuessUtf16(bytes) is { } utf16)
        {
            return (utf16, true);
        }

        return IsValid(bytes, TextEncodingId.Utf8)
            ? (TextEncodingId.Utf8, false)
            : (TextEncodingId.Windows1252, true);
    }

    /// <summary>Decodes the bytes, reporting whether any sequence was invalid and had to be replaced by U+FFFD.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, TextEncodingId id, out bool hadInvalidBytes)
    {
        bytes = bytes[GetBomLength(bytes, id)..];
        try
        {
            hadInvalidBytes = false;
            return GetEncoding(id, strict: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            hadInvalidBytes = true;
            return GetEncoding(id, strict: false).GetString(bytes);
        }
    }

    /// <summary>Encodes text, including the BOM the encoding calls for.</summary>
    public static byte[] Encode(string text, TextEncodingId id)
    {
        var encoding = GetEncoding(id, strict: false);
        var preamble = HasBom(id) ? encoding.GetPreamble() : [];
        var result = new byte[preamble.Length + encoding.GetByteCount(text)];
        preamble.CopyTo(result, 0);
        encoding.GetBytes(text, result.AsSpan(preamble.Length));
        return result;
    }

    /// <summary>False when saving in this encoding would lose characters (for example emoji in Windows-1252).</summary>
    public static bool CanEncode(string text, TextEncodingId id)
    {
        try
        {
            GetEncoding(id, strict: true).GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public static bool HasBom(TextEncodingId id) =>
        id is TextEncodingId.Utf8Bom or TextEncodingId.Utf16Le or TextEncodingId.Utf16Be;

    private static bool IsValid(ReadOnlySpan<byte> bytes, TextEncodingId id)
    {
        try
        {
            GetEncoding(id, strict: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int GetBomLength(ReadOnlySpan<byte> bytes, TextEncodingId id) => id switch
    {
        TextEncodingId.Utf8 or TextEncodingId.Utf8Bom when bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]) => 3,
        TextEncodingId.Utf16Le when bytes.StartsWith<byte>([0xFF, 0xFE]) => 2,
        TextEncodingId.Utf16Be when bytes.StartsWith<byte>([0xFE, 0xFF]) => 2,
        _ => 0,
    };

    private static TextEncodingId? GuessUtf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes.Length % 2 != 0)
        {
            return null;
        }

        var sample = bytes[..Math.Min(bytes.Length, 64 * 1024)];
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i + 1 < sample.Length; i += 2)
        {
            if (sample[i] == 0)
            {
                evenZeros++;
            }

            if (sample[i + 1] == 0)
            {
                oddZeros++;
            }
        }

        var pairs = sample.Length / 2;
        if (oddZeros * 10 >= pairs * 3 && evenZeros * 20 <= pairs && IsValid(bytes, TextEncodingId.Utf16Le))
        {
            return TextEncodingId.Utf16Le;
        }

        if (evenZeros * 10 >= pairs * 3 && oddZeros * 20 <= pairs && IsValid(bytes, TextEncodingId.Utf16Be))
        {
            return TextEncodingId.Utf16Be;
        }

        return null;
    }

    private static Encoding GetEncoding(TextEncodingId id, bool strict) => id switch
    {
        TextEncodingId.Utf8 or TextEncodingId.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: strict),
        TextEncodingId.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: strict),
        TextEncodingId.Utf16Be => new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: strict),
        TextEncodingId.Windows1252 => strict
            ? Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            : Encoding.GetEncoding(1252),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}
