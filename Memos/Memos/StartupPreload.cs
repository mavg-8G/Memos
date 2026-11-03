using Memos.Core.Services;

namespace Memos;

/// <summary>
/// Reads the settings file on the thread pool while the application starts, instead of on the UI
/// thread right before the window is created. The result is the same service instance: it is
/// constructed once, off the critical path, and handed to the container when it is first needed.
/// </summary>
internal static class StartupPreload
{
    private static Task<JsonSettingsService>? _settings;

    /// <summary>Local data folder: MEMOS_DATA_DIR when set, otherwise %LOCALAPPDATA%\Memos.</summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>Starts reading the settings in the background. Safe to call once per process.</summary>
    public static void Begin()
    {
        if (_settings is not null)
        {
            return;
        }

        var paths = new AppPaths(DataDirectory);
        _settings = Task.Run(() => new JsonSettingsService(paths));
    }

    /// <summary>
    /// The settings service, already loaded in the common case. If <see cref="Begin"/> was not
    /// called (tests, tooling), it is loaded now.
    /// </summary>
    public static ISettingsService TakeSettings()
    {
        Begin();
        return _settings!.GetAwaiter().GetResult();
    }

    private static string ResolveDataDirectory() =>
        Environment.GetEnvironmentVariable("MEMOS_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Memos");
}
