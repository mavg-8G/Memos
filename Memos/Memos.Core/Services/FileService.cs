using Memos.Core.Models;

namespace Memos.Core.Services;

public interface IFileService
{
    /// <summary>Size in bytes, or null if the file does not exist or cannot be queried.</summary>
    long? GetLength(string path);

    FileStamp? GetStamp(string path);

    /// <summary>Reads a text file. With <paramref name="forcedEncoding"/> the detection is skipped.</summary>
    Task<LoadedText> LoadAsync(string path, TextEncodingId? forcedEncoding = null, CancellationToken cancellationToken = default);

    Task<LoadedRichText> LoadRichTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes '\n'-separated text with the given encoding and line ending, replacing the file atomically when possible.</summary>
    Task<FileStamp> SaveAsync(string path, string text, TextEncodingId encoding, LineEnding lineEnding, CancellationToken cancellationToken = default);

    Task<FileStamp> SaveRichTextAsync(string path, string rtf, CancellationToken cancellationToken = default);
}

/// <summary>Limits that keep the editing control responsive and its behaviour predictable.</summary>
public static class FileLimits
{
    /// <summary>
    /// Above this size the user is asked before opening: the editing control lays out the whole
    /// document up front, which takes seconds for a few megabytes and much longer when the text is
    /// rich in non-ASCII characters.
    /// </summary>
    public const long WarnBytes = 2L * 1024 * 1024;

    /// <summary>Above this size files are refused; the control cannot handle them acceptably.</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    /// <summary>Above this many characters live Markdown highlighting in the editor is turned off.</summary>
    public const int MaxHighlightChars = 300_000;
}

public sealed class FileTooLargeException(string path, long length)
    : IOException($"The file is too large ({length} bytes): {path}")
{
    public long Length { get; } = length;
}

/// <summary>The file holds NUL characters, so it is not text the editor can round-trip.</summary>
public sealed class BinaryFileException(string path) : IOException($"The file is not a text file: {path}");

public sealed class ReadOnlyFileException(string path) : IOException($"The file is read-only: {path}");

public sealed class FileService : IFileService
{
    public long? GetLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return null;
        }
    }

    public FileStamp? GetStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return null;
        }
    }

    public async Task<LoadedText> LoadAsync(string path, TextEncodingId? forcedEncoding = null, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(null, path);
        }

        if (info.Length > FileLimits.MaxBytes)
        {
            throw new FileTooLargeException(path, info.Length);
        }

        byte[] bytes;
        FileStamp stamp;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            stamp = new FileStamp(File.GetLastWriteTimeUtc(path), bytes.Length);
        }

        return await Task.Run(() => Decode(path, bytes, forcedEncoding, stamp), cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileStamp> SaveAsync(string path, string text, TextEncodingId encoding, LineEnding lineEnding, CancellationToken cancellationToken = default)
    {
        var bytes = await Task.Run(() => TextEncodings.Encode(LineEndings.Apply(text, lineEnding), encoding), cancellationToken).ConfigureAwait(false);
        return await SaveBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoadedRichText> LoadRichTextAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(null, path);
        if (info.Length > FileLimits.MaxBytes) throw new FileTooLargeException(path, info.Length);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().StartsWith("{\\rtf"u8)) throw new IOException("The file is not an RTF document.");
        return new LoadedRichText(System.Text.Encoding.Latin1.GetString(bytes), GetStamp(path) ?? default);
    }

    public Task<FileStamp> SaveRichTextAsync(string path, string rtf, CancellationToken cancellationToken = default) =>
        SaveBytesAsync(path, System.Text.Encoding.Latin1.GetBytes(Helpers.RichTextCodec.EscapeNonLatin(rtf)), cancellationToken);

    private async Task<FileStamp> SaveBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path) ?? throw new IOException($"Invalid path: {path}");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        var exists = File.Exists(path);
        if (exists && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
        {
            throw new ReadOnlyFileException(path);
        }

        // Write next to the target and swap, so a crash or a full disk never leaves a half-written file.
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var tempCreated = false;
        try
        {
            try
            {
                await using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
                tempCreated = true;
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            catch (UnauthorizedAccessException) when (!tempCreated && exists)
            {
                // The folder does not allow creating files but the file itself may be writable.
                await WriteInPlaceAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                return GetStamp(path) ?? default;
            }

            if (exists)
            {
                try
                {
                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                }
                catch (IOException) when (File.Exists(temp))
                {
                    // Some file systems (network shares, FAT) cannot replace atomically.
                    await WriteInPlaceAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (tempCreated)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                }
            }
        }

        return GetStamp(path) ?? default;
    }

    public static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static async Task WriteInPlaceAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static LoadedText Decode(string path, byte[] bytes, TextEncodingId? forcedEncoding, FileStamp stamp)
    {
        var (encoding, guessed) = forcedEncoding is { } forced ? (forced, false) : TextEncodings.Detect(bytes);
        var raw = TextEncodings.Decode(bytes, encoding, out var hadInvalidBytes);
        if (raw.Contains('\0'))
        {
            throw new BinaryFileException(path);
        }

        var (lineEnding, mixed) = LineEndings.Detect(raw);
        return new LoadedText(LineEndings.Normalize(raw), encoding, lineEnding, mixed, hadInvalidBytes, guessed, stamp);
    }
}

public static class LineEndings
{
    public static string GetDisplayName(LineEnding lineEnding) => lineEnding switch
    {
        LineEnding.CrLf => "CRLF",
        LineEnding.Lf => "LF",
        _ => "CR",
    };

    /// <summary>Most frequent line ending, or null when the text has no line breaks at all.</summary>
    public static (LineEnding? Dominant, bool Mixed) Detect(ReadOnlySpan<char> text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        if (crlf + lf + cr == 0)
        {
            return (null, false);
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var dominant = crlf >= lf && crlf >= cr ? LineEnding.CrLf : lf >= cr ? LineEnding.Lf : LineEnding.Cr;
        return (dominant, kinds > 1);
    }

    /// <summary>Converts every kind of line break to '\n'.</summary>
    public static string Normalize(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    public static string Apply(string text, LineEnding lineEnding) => lineEnding switch
    {
        LineEnding.CrLf => text.Replace("\n", "\r\n"),
        LineEnding.Cr => text.Replace('\n', '\r'),
        _ => text,
    };
}
