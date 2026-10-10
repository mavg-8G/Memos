using Memos.Core.Services;
using Memos.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Memos.Services;

/// <summary>
/// The single WebView2 environment shared by the preview and the PDF export, locked down so that
/// Markdown content can never run code or reach the network on its own.
/// </summary>
public sealed class WebViewEnvironment(IAppPaths paths)
{
    /// <summary>Virtual host that serves the app's own temporary export pages.</summary>
    public const string AppHost = "memos-app.example";

    private Task<CoreWebView2Environment>? _environment;

    public string TempDirectory { get; } = Path.Combine(paths.DataDirectory, "Temp");

    public Task<CoreWebView2Environment> GetAsync() => _environment ??= CreateAsync();

    /// <summary>Creates the browser core for a control and applies the restrictions every view shares.</summary>
    public async Task<CoreWebView2> InitializeAsync(WebView2 view)
    {
        await view.EnsureCoreWebView2Async(await GetAsync());
        var core = view.CoreWebView2;
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
        return core;
    }

    /// <summary>Makes the document's folder reachable (for local images) under the preview host.</summary>
    public static void MapDocumentFolder(CoreWebView2 core, string? directory)
    {
        core.ClearVirtualHostNameToFolderMapping(DocumentViewModel.PreviewHost);
        if (directory is not null && Directory.Exists(directory))
        {
            core.SetVirtualHostNameToFolderMapping(DocumentViewModel.PreviewHost, directory, CoreWebView2HostResourceAccessKind.Allow);
        }
    }

    private async Task<CoreWebView2Environment> CreateAsync()
    {
        var userData = Path.Combine(paths.DataDirectory, "WebView2");
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
