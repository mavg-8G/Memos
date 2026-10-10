using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Memos.Core.Helpers;
using Memos.Core.Models;

namespace Memos.Core.Services;

/// <summary>Where the application keeps its own data (settings, recent files, session backups).</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
}

public sealed class AppPaths(string dataDirectory) : IAppPaths
{
    public string DataDirectory { get; } = dataDirectory;
}

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Writes pending changes now; called when the application closes.</summary>
    Task FlushAsync();
}

public interface IRecentFilesService
{
    IReadOnlyList<string> Items { get; }

    event EventHandler? Changed;

    void Add(string path);

    void Remove(string path);

    void Clear();
}

public interface ISessionService
{
    Task<SessionState?> LoadAsync();

    Task<string?> ReadBackupAsync(Guid documentId);

    /// <summary>
    /// Stores the session. <paramref name="changedBackups"/> holds only the document texts that changed
    /// since the last save; backups of documents no longer in the session are deleted.
    /// </summary>
    Task SaveAsync(SessionState state, IReadOnlyDictionary<Guid, string> changedBackups);

    void Clear();
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

internal static class JsonFile
{
    public static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), typeInfo) : null;
        }
        catch (Exception ex) when (ex is JsonException || FileService.IsFileSystemError(ex))
        {
            return null;
        }
    }

    public static async Task WriteAsync<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private readonly string _path;
    private readonly Debouncer _saveDebouncer = new();
    private bool _dirty;

    public JsonSettingsService(IAppPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "settings.json");
        Current = JsonFile.Read(_path, CoreJsonContext.Default.AppSettings) ?? new AppSettings();
        Current.Normalize();
        Current.PropertyChanged += OnSettingChanged;
    }

    public AppSettings Current { get; }

    public async Task FlushAsync()
    {
        _saveDebouncer.Cancel();
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        try
        {
            await JsonFile.WriteAsync(_path, Current, CoreJsonContext.Default.AppSettings).ConfigureAwait(false);
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
            // Preferences are best effort: failing to store them must never break editing.
        }
    }

    /// <summary>Marks the settings as changed without going through a property (window placement).</summary>
    public void MarkDirty() => _dirty = true;

    public void Dispose() => _saveDebouncer.Dispose();

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        _dirty = true;
        _saveDebouncer.Run(TimeSpan.FromMilliseconds(600), _ => FlushAsync());
    }
}

public sealed class RecentFilesService : IRecentFilesService
{
    private const int MaxItems = 15;
    private readonly string _path;
    private readonly List<string> _items;

    public RecentFilesService(IAppPaths paths)
    {
        _path = Path.Combine(paths.DataDirectory, "recent.json");
        _items = JsonFile.Read(_path, CoreJsonContext.Default.ListString) ?? [];
    }

    public IReadOnlyList<string> Items => _items;

    public event EventHandler? Changed;

    public void Add(string path)
    {
        if (_items.Count > 0 && string.Equals(_items[0], path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, path);
        if (_items.Count > MaxItems)
        {
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        }

        Save();
    }

    public void Remove(string path)
    {
        if (_items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    public void Clear()
    {
        if (_items.Count > 0)
        {
            _items.Clear();
            Save();
        }
    }

    private async void Save()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await JsonFile.WriteAsync(_path, _items.ToList(), CoreJsonContext.Default.ListString).ConfigureAwait(false);
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
        }
    }
}

public sealed class SessionService(IAppPaths paths) : ISessionService
{
    private readonly string _directory = Path.Combine(paths.DataDirectory, "Session");

    private string StatePath => Path.Combine(_directory, "session.json");

    public Task<SessionState?> LoadAsync() =>
        Task.Run(() => JsonFile.Read(StatePath, CoreJsonContext.Default.SessionState));

    public async Task<string?> ReadBackupAsync(Guid documentId)
    {
        try
        {
            var path = GetBackupPath(documentId);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
            return null;
        }
    }

    public async Task SaveAsync(SessionState state, IReadOnlyDictionary<Guid, string> changedBackups)
    {
        Directory.CreateDirectory(_directory);
        foreach (var (id, text) in changedBackups)
        {
            var path = GetBackupPath(id);
            await File.WriteAllTextAsync(path + ".tmp", text, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(path + ".tmp", path, overwrite: true);
        }

        await JsonFile.WriteAsync(StatePath, state, CoreJsonContext.Default.SessionState).ConfigureAwait(false);

        var keep = state.Documents.Where(d => d.HasBackup).Select(d => GetBackupPath(d.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.bak"))
        {
            if (!keep.Contains(file))
            {
                File.Delete(file);
            }
        }
    }

    public void Clear()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex))
        {
        }
    }

    private string GetBackupPath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".bak");
}
