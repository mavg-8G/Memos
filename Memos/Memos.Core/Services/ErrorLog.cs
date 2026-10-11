using System.Text;

namespace Memos.Core.Services;

/// <summary>
/// Local record of unexpected errors. It never leaves the machine; the user can read it, copy it
/// or delete it from Settings when reporting a problem.
/// </summary>
public interface IErrorLog
{
    string FilePath { get; }

    bool HasEntries { get; }

    void Write(Exception exception, string? context = null);

    /// <summary>The whole log, or null when there is none.</summary>
    string? Read();

    void Clear();
}

public sealed class ErrorLog(IAppPaths paths) : IErrorLog
{
    /// <summary>The file is restarted when it grows past this size, so it can never fill the disk.</summary>
    private const long MaxBytes = 512 * 1024;

    private readonly Lock _gate = new();

    public string FilePath { get; } = Path.Combine(paths.DataDirectory, "crash.log");

    public bool HasEntries
    {
        get
        {
            try
            {
                return new FileInfo(FilePath) is { Exists: true, Length: > 0 };
            }
            catch (Exception ex) when (FileService.IsFileSystemError(ex))
            {
                return false;
            }
        }
    }

    public void Write(Exception exception, string? context = null)
    {
        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O"))
            .Append(context is null ? "" : $"  [{context}]")
            .Append('\n')
            .Append(exception)
            .Append("\n\n")
            .ToString();

        // Logging must never cause a second failure.
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (new FileInfo(FilePath) is { Exists: true, Length: > MaxBytes })
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(FilePath, entry, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
        }
    }

    public string? Read()
    {
        try
        {
            lock (_gate)
            {
                return File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : null;
            }
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
        }
    }
}
