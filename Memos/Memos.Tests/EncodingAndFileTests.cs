using System.Text;
using Memos.Core.Models;
using Memos.Core.Services;

namespace Memos.Tests;

public class EncodingTests
{
    // Spanish, Greek, Cyrillic, Arabic, Hebrew, Chinese, Japanese, Korean, a ZWJ emoji family,
    // a flag, a skin-tone modifier, a supplementary-plane character and a combining sequence.
    public const string International =
        "Año, pingüino, ¿qué? — Ελληνικά — Кириллица — العربية — עברית — 中文 — 日本語 — 한국어 — " +
        "👨‍👩‍👧‍👦 🇪🇸 👍🏽 𝄞 𠀀 e\u0301 n\u0303";

    [Theory]
    [InlineData(TextEncodingId.Utf8)]
    [InlineData(TextEncodingId.Utf8Bom)]
    [InlineData(TextEncodingId.Utf16Le)]
    [InlineData(TextEncodingId.Utf16Be)]
    public void Unicode_round_trips_and_is_detected(TextEncodingId encoding)
    {
        var bytes = TextEncodings.Encode(International, encoding);

        var (detected, guessed) = TextEncodings.Detect(bytes);
        var decoded = TextEncodings.Decode(bytes, detected, out var invalid);

        Assert.Equal(encoding, detected);
        Assert.False(guessed);
        Assert.False(invalid);
        Assert.Equal(International, decoded);
    }

    [Fact]
    public void Bom_is_written_only_when_the_encoding_asks_for_it()
    {
        Assert.Equal(new byte[] { (byte)'a' }, TextEncodings.Encode("a", TextEncodingId.Utf8));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }, TextEncodings.Encode("a", TextEncodingId.Utf8Bom));
        Assert.Equal(new byte[] { 0xFF, 0xFE, (byte)'a', 0 }, TextEncodings.Encode("a", TextEncodingId.Utf16Le));
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0, (byte)'a' }, TextEncodings.Encode("a", TextEncodingId.Utf16Be));
    }

    [Fact]
    public void Utf16_without_bom_is_recognised()
    {
        var le = Encoding.Unicode.GetBytes("plain ascii text without a byte order mark");
        var be = Encoding.BigEndianUnicode.GetBytes("plain ascii text without a byte order mark");

        Assert.Equal((TextEncodingId.Utf16Le, true), TextEncodings.Detect(le));
        Assert.Equal((TextEncodingId.Utf16Be, true), TextEncodings.Detect(be));
    }

    [Fact]
    public void Bytes_that_are_not_utf8_fall_back_to_windows_1252_losslessly()
    {
        byte[] bytes = [(byte)'a', 0xF1, (byte)'o', 0x80]; // "año€" in Windows-1252

        var (detected, guessed) = TextEncodings.Detect(bytes);
        var text = TextEncodings.Decode(bytes, detected, out var invalid);

        Assert.Equal(TextEncodingId.Windows1252, detected);
        Assert.True(guessed);
        Assert.False(invalid);
        Assert.Equal("año€", text);
        Assert.Equal(bytes, TextEncodings.Encode(text, detected));
    }

    [Fact]
    public void Invalid_sequences_are_reported_when_an_encoding_is_forced()
    {
        byte[] bytes = [(byte)'a', 0xFF, (byte)'b'];

        var text = TextEncodings.Decode(bytes, TextEncodingId.Utf8, out var invalid);

        Assert.True(invalid);
        Assert.Equal("a\uFFFDb", text);
    }

    [Fact]
    public void Lossy_conversions_are_detected_before_saving()
    {
        Assert.True(TextEncodings.CanEncode("año", TextEncodingId.Windows1252));
        Assert.False(TextEncodings.CanEncode("日本語 👍", TextEncodingId.Windows1252));
        Assert.True(TextEncodings.CanEncode(International, TextEncodingId.Utf8));
        Assert.False(TextEncodings.CanEncode("lone \uD83D surrogate", TextEncodingId.Utf8));
    }
}

public class FileServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileService _files = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(LineEnding.CrLf, "\r\n")]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public async Task Line_endings_survive_a_load_and_save(LineEnding expected, string separator)
    {
        var path = _temp.File("lines.txt");
        var original = Encoding.UTF8.GetBytes($"uno{separator}dos{separator}{separator}tres{separator}");
        File.WriteAllBytes(path, original);

        var loaded = await _files.LoadAsync(path);
        await _files.SaveAsync(path, loaded.Text, loaded.Encoding, loaded.LineEnding!.Value);

        Assert.Equal(expected, loaded.LineEnding);
        Assert.False(loaded.HasMixedLineEndings);
        Assert.Equal("uno\ndos\n\ntres\n", loaded.Text);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(TextEncodingId.Utf8)]
    [InlineData(TextEncodingId.Utf8Bom)]
    [InlineData(TextEncodingId.Utf16Le)]
    [InlineData(TextEncodingId.Utf16Be)]
    public async Task International_text_is_preserved_byte_for_byte(TextEncodingId encoding)
    {
        var path = _temp.File("unicode.txt");
        var original = TextEncodings.Encode(EncodingTests.International + "\r\nשלום עולם\r\nمرحبا بالعالم\r\n", encoding);
        File.WriteAllBytes(path, original);

        var loaded = await _files.LoadAsync(path);
        await _files.SaveAsync(path, loaded.Text, loaded.Encoding, loaded.LineEnding!.Value);

        Assert.Equal(encoding, loaded.Encoding);
        Assert.Contains("👨‍👩‍👧‍👦", loaded.Text);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Mixed_line_endings_are_flagged_and_the_dominant_one_is_reported()
    {
        var path = _temp.File("mixed.txt");
        File.WriteAllText(path, "a\r\nb\r\nc\nd");

        var loaded = await _files.LoadAsync(path);

        Assert.True(loaded.HasMixedLineEndings);
        Assert.Equal(LineEnding.CrLf, loaded.LineEnding);
    }

    [Fact]
    public async Task Empty_file_loads_as_empty_text_and_saves_as_zero_bytes()
    {
        var path = _temp.File("empty.txt");
        File.WriteAllBytes(path, []);

        var loaded = await _files.LoadAsync(path);
        await _files.SaveAsync(path, loaded.Text, loaded.Encoding, LineEnding.CrLf);

        Assert.Equal("", loaded.Text);
        Assert.Null(loaded.LineEnding);
        Assert.Equal(TextEncodingId.Utf8, loaded.Encoding);
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Large_document_round_trips()
    {
        var path = _temp.File("large.txt");
        var text = string.Join('\n', Enumerable.Range(0, 60_000).Select(i => $"línea {i} — 日本語 👍 texto de relleno para alcanzar varios megabytes"));

        await _files.SaveAsync(path, text, TextEncodingId.Utf8, LineEnding.CrLf);
        var loaded = await _files.LoadAsync(path);

        Assert.True(new FileInfo(path).Length > 3_000_000);
        Assert.Equal(text, loaded.Text);
        Assert.Equal(LineEnding.CrLf, loaded.LineEnding);
    }

    [Fact]
    public async Task Files_over_the_limit_are_refused()
    {
        var path = _temp.File("huge.log");
        using (var stream = File.Create(path))
        {
            stream.SetLength(FileLimits.MaxBytes + 1);
        }

        await Assert.ThrowsAsync<FileTooLargeException>(() => _files.LoadAsync(path));
    }

    [Fact]
    public async Task Binary_content_is_refused_instead_of_being_corrupted()
    {
        var path = _temp.File("image.bin");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x00, 0x00, 0xFF]);

        await Assert.ThrowsAsync<BinaryFileException>(() => _files.LoadAsync(path));
    }

    [Fact]
    public async Task Missing_file_and_missing_folder_fail_cleanly()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _files.LoadAsync(_temp.File("nope.txt")));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _files.SaveAsync(_temp.File(@"no\such\folder\a.txt"), "x", TextEncodingId.Utf8, LineEnding.Lf));
        Assert.Null(_files.GetLength(_temp.File("nope.txt")));
    }

    [Fact]
    public async Task Invalid_path_is_reported_as_a_file_system_error()
    {
        var ex = await Record.ExceptionAsync(() => _files.LoadAsync("C:\\bad\0name.txt"));

        Assert.NotNull(ex);
        Assert.True(FileService.IsFileSystemError(ex));
    }

    [Fact]
    public async Task Read_only_file_is_not_overwritten()
    {
        var path = _temp.File("readonly.txt");
        File.WriteAllText(path, "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        await Assert.ThrowsAsync<ReadOnlyFileException>(() => _files.SaveAsync(path, "changed", TextEncodingId.Utf8, LineEnding.Lf));

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public async Task A_file_locked_by_another_program_keeps_its_content_and_no_temp_file_is_left()
    {
        var path = _temp.File("locked.txt");
        File.WriteAllText(path, "original");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Record.ExceptionAsync(() => _files.SaveAsync(path, "changed", TextEncodingId.Utf8, LineEnding.Lf));
            Assert.True(ex is IOException or UnauthorizedAccessException);
        }

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public async Task Saving_replaces_the_file_without_leaving_temporary_files()
    {
        var path = _temp.File("note.md");

        var first = await _files.SaveAsync(path, "uno", TextEncodingId.Utf8, LineEnding.Lf);
        var second = await _files.SaveAsync(path, "uno\ndos", TextEncodingId.Utf8, LineEnding.CrLf);

        Assert.Equal("uno\r\ndos", File.ReadAllText(path));
        Assert.Equal(3, first.Length);
        Assert.Equal(8, second.Length);
        Assert.Equal(second, _files.GetStamp(path));
        Assert.Single(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public async Task Changing_encoding_converts_the_bytes_on_disk()
    {
        var path = _temp.File("convert.txt");
        File.WriteAllBytes(path, TextEncodings.Encode("canción ñandú", TextEncodingId.Windows1252));

        var loaded = await _files.LoadAsync(path);
        await _files.SaveAsync(path, loaded.Text, TextEncodingId.Utf16Le, LineEnding.CrLf);
        var reloaded = await _files.LoadAsync(path);

        Assert.Equal(TextEncodingId.Windows1252, loaded.Encoding);
        Assert.Equal(TextEncodingId.Utf16Le, reloaded.Encoding);
        Assert.Equal("canción ñandú", reloaded.Text);
    }

    [Fact]
    public async Task Forcing_an_encoding_reinterprets_the_same_bytes()
    {
        var path = _temp.File("forced.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("ñ"));

        var asAnsi = await _files.LoadAsync(path, TextEncodingId.Windows1252);

        Assert.Equal("Ã±", asAnsi.Text);
    }
}
