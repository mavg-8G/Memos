using Memos.Core.Services;
using Memos.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Globalization;

namespace Memos.Services;

/// <summary>
/// The single WebView2 environment shared by the preview and the PDF export, locked down so that
/// Markdown content can never run code or reach the network on its own.
/// </summary>
public sealed class WebViewEnvironment(IAppPaths paths, IErrorLog log)
{
    /// <summary>A preview that takes longer than this to appear is written down with its timings.</summary>
    private static readonly TimeSpan SlowStart = TimeSpan.FromSeconds(5);

    /// <summary>Virtual host that serves the app's own temporary export pages.</summary>
    public const string AppHost = "memos-app.example";

    private Task<CoreWebView2Environment>? _environment;
    private readonly HashSet<string> _failedFolders = new(StringComparer.OrdinalIgnoreCase);
    private string? _userData;
    private WebView2? _spare;
    private bool _shutDown;

    public string TempDirectory { get; } = Path.Combine(paths.DataDirectory, "Temp");

    public Task<CoreWebView2Environment> GetAsync()
    {
        // A failed attempt is not kept, so the next preview tries again instead of failing forever.
        if (_environment is null || _environment.IsFaulted || _environment.IsCanceled)
        {
            _environment = CreateAsync();
        }

        return _environment;
    }

    /// <summary>Creates the browser core for a control and applies the restrictions every view shares.</summary>
    public async Task<CoreWebView2> InitializeAsync(WebView2 view)
    {
        // The control does not throw when the browser cannot start: it reports the reason through
        // this event and leaves its CoreWebView2 empty.
        Exception? failure = null;
        void OnInitialized(WebView2 sender, CoreWebView2InitializedEventArgs e) => failure = e.Exception;
        view.CoreWebView2Initialized += OnInitialized;
        try
        {
            await view.EnsureCoreWebView2Async(await GetAsync());
        }
        finally
        {
            view.CoreWebView2Initialized -= OnInitialized;
        }

        if (view.CoreWebView2 is not { } core)
        {
            // Typically the browser of an earlier session is still closing and its folder is busy.
            // The next attempt gets a new environment on another folder.
            if (_userData is not null)
            {
                _failedFolders.Add(_userData);
            }

            _environment = null;
            throw new InvalidOperationException("The browser of the preview could not be started.", failure);
        }

        var settings = core.Settings;

        // Page scripts never run. The app itself can still update the page through ExecuteScriptAsync.
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsReputationCheckingRequired = false;

        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        if (_userData is not null)
        {
            RememberBrowser(_userData, (int)core.BrowserProcessId);
        }

        return core;
    }

    /// <summary>
    /// Takes back the view of a preview that is going away. One is kept instead of being closed:
    /// when the last view closes, the browser process starts to shut down, which can take it a
    /// minute, and a preview opened in the meantime shows nothing until that is over.
    /// </summary>
    public void Release(WebView2 view)
    {
        if (_spare is null && !_shutDown && view.CoreWebView2 is { } core)
        {
            try
            {
                // Nothing of the document it showed stays in it.
                core.ClearVirtualHostNameToFolderMapping(DocumentViewModel.PreviewHost);
                core.NavigateToString("");
                _spare = view;
                return;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The browser behind it is gone; it cannot be reused.
            }
        }

        view.Close();
    }

    /// <summary>A view left by <see cref="Release"/>, ready to show a page at once; or null.</summary>
    public WebView2? TakeSpare()
    {
        var spare = _spare;
        _spare = null;
        return spare;
    }

    /// <summary>Closes the view that was being kept; from here on views are closed, not kept.</summary>
    public void Shutdown()
    {
        _shutDown = true;
        TakeSpare()?.Close();
    }

    /// <summary>
    /// The browser normally starts in well under a second. When it does not, the time of each step
    /// goes to the local log: it tells a slow browser start from a slow page.
    /// </summary>
    public void ReportIfSlow(TimeSpan browserStart, TimeSpan pageLoad)
    {
        if (browserStart + pageLoad > SlowStart)
        {
            log.Write(new TimeoutException(
                $"The preview took {(browserStart + pageLoad).TotalSeconds:0.0} s to start: browser {browserStart.TotalSeconds:0.0} s, page {pageLoad.TotalSeconds:0.0} s."), "Preview");
        }
    }

    public void Report(Exception exception) => log.Write(exception, "Preview");

    /// <summary>Makes the document's folder reachable (for local images) under the preview host.</summary>
    public static void MapDocumentFolder(CoreWebView2 core, string? directory)
    {
        core.ClearVirtualHostNameToFolderMapping(DocumentViewModel.PreviewHost);
        if (directory is not null && Directory.Exists(directory))
        {
            core.SetVirtualHostNameToFolderMapping(DocumentViewModel.PreviewHost, directory, CoreWebView2HostResourceAccessKind.Allow);
        }
    }

    /// <summary>
    /// Folders the browser may keep its data in. There is more than one because a folder cannot be
    /// used while the browser of an earlier session is still closing, which can take a minute: it
    /// happens when Memos is opened again right after being closed. Nothing of value lives in them
    /// (the preview has no state), so any of them does.
    /// </summary>
    private static readonly string[] UserDataFolders = ["WebView2", "WebView2-b", "WebView2-c"];

    /// <summary>Name of the file, inside a data folder, with the process id and start time of its browser.</summary>
    private const string BrowserFileName = "browser.pid";

    private string ChooseUserDataDirectory()
    {
        var candidates = UserDataFolders.Select(name => Path.Combine(paths.DataDirectory, name)).ToList();
        if (candidates.TrueForAll(_failedFolders.Contains))
        {
            // Every folder has failed once: start over rather than give up.
            _failedFolders.Clear();
        }

        candidates.RemoveAll(_failedFolders.Contains);
        return candidates.Find(folder => !IsBrowserStillThere(folder)) ?? candidates[0];
    }

    /// <summary>Whether the browser that last used a folder still exists, even if it is on its way out.</summary>
    private static bool IsBrowserStillThere(string folder)
    {
        try
        {
            var file = Path.Combine(folder, BrowserFileName);
            if (!File.Exists(file) ||
                File.ReadAllText(file).Split(' ') is not [var id, var ticks] ||
                !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
                !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var started))
            {
                return false;
            }

            using var process = Process.GetProcessById(processId);
            return GetStartTicks(process) is not { } actual || actual == started;
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex) || ex is ArgumentException or InvalidOperationException)
        {
            // No such process any more, or nothing readable about the folder.
            return false;
        }
    }

    private void RememberBrowser(string folder, int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (GetStartTicks(process) is { } started)
            {
                File.WriteAllText(Path.Combine(folder, BrowserFileName), string.Create(CultureInfo.InvariantCulture, $"{processId} {started}"));
            }
        }
        catch (Exception ex) when (FileService.IsFileSystemError(ex) || ex is ArgumentException or InvalidOperationException)
        {
        }
    }

    /// <summary>Start time of a process, which tells it apart from a later one that got the same id.</summary>
    private static long? GetStartTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<CoreWebView2Environment> CreateAsync()
    {
        var userData = _userData = ChooseUserDataDirectory();
        Directory.CreateDirectory(userData);
        var options = new CoreWebView2EnvironmentOptions
        {
            // Keep the embedded browser from talking to the network on its own.
            AdditionalBrowserArguments =
                "--disable-background-networking --disable-component-update --disable-sync --disable-domain-reliability --no-first-run --no-pings",
        };
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, options);
    }
}

/// <summary>Prints HTML to PDF with a temporary, invisible WebView2 that is disposed afterwards.</summary>
public sealed class WebViewPdfExporter(WebViewEnvironment environment, WindowContext context) : IPdfExporter
{
    public async Task ExportAsync(string html, string? baseDirectory, string outputPath)
    {
        var host = context.HiddenHost ?? throw new InvalidOperationException("The window is not ready.");
        var pageName = $"export-{Guid.NewGuid():N}.html";
        var pagePath = Path.Combine(environment.TempDirectory, pageName);
        var view = new WebView2 { Width = 794, Height = 1123, Opacity = 0, IsHitTestVisible = false, IsTabStop = false };
        host.Children.Add(view);
        try
        {
            // NavigateToString is limited to 2 MB, so the page is served from a private temp folder.
            Directory.CreateDirectory(environment.TempDirectory);
            await File.WriteAllTextAsync(pagePath, html, new System.Text.UTF8Encoding(false));

            var core = await environment.InitializeAsync(view);
            core.SetVirtualHostNameToFolderMapping(WebViewEnvironment.AppHost, environment.TempDirectory, CoreWebView2HostResourceAccessKind.Deny);
            WebViewEnvironment.MapDocumentFolder(core, baseDirectory);

            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate($"https://{WebViewEnvironment.AppHost}/{pageName}");
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("The document could not be rendered.");
            }

            if (File.Exists(outputPath) && File.GetAttributes(outputPath).HasFlag(FileAttributes.ReadOnly))
            {
                throw new ReadOnlyFileException(outputPath);
            }

            var settings = (await environment.GetAsync()).CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            if (!await core.PrintToPdfAsync(outputPath, settings))
            {
                throw new IOException("The PDF file could not be written.");
            }
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The document took too long to render.");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Usually the WebView2 runtime is missing or could not start.
            throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            host.Children.Remove(view);
            view.Close();
            try
            {
                File.Delete(pagePath);
            }
            catch (Exception ex) when (FileService.IsFileSystemError(ex))
            {
            }
        }
    }
}
