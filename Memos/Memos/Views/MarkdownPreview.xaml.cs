using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;
using Memos.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Globalization;

namespace Memos.Views;

/// <summary>
/// Rendered view of a Markdown document. The page never runs scripts of its own; the app updates it
/// by replacing only the blocks that changed, which keeps typing smooth and the scroll position stable.
/// </summary>
public sealed partial class MarkdownPreview : UserControl
{
    private const int StartAttempts = 6;

    private WebViewEnvironment? _environment;
    private AppSettings? _settings;
    private ILocalizer? _localizer;
    private WebView2? _view;
    private CoreWebView2? _core;
    private IReadOnlyList<MarkdownBlock> _shownBlocks = [];
    private string? _pageKey;
    private string? _mappedDirectory;
    private bool _directoryMapped;
    private bool _loadingPage;
    private bool _busy;
    private bool _closed;
    private (MarkdownAnalysis? Analysis, string? Directory) _requested;
    private (MarkdownAnalysis? Analysis, string? Directory, string? PageKey) _applied;
    private (double Line, bool AtEnd)? _pendingScroll;

    public MarkdownPreview()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Refresh();
    }

    /// <summary>The user clicked a link to a local file next to the document.</summary>
    public event EventHandler<string>? LocalFileActivated;

    public void Configure(WebViewEnvironment environment, AppSettings settings, ILocalizer localizer)
    {
        _environment = environment;
        _settings = settings;
        _localizer = localizer;
    }

    /// <summary>Shows the analysis. Calls made while an update is running collapse into the latest one.</summary>
    public async void Show(MarkdownAnalysis? analysis, string? directory)
    {
        _requested = (analysis, directory);
        if (_busy || _closed || _environment is null)
        {
            return;
        }

        _busy = true;
        try
        {
            while (!_closed && (_requested.Analysis != _applied.Analysis || _requested.Directory != _applied.Directory || GetPageKey(_requested.Directory) != _applied.PageKey))
            {
                var request = _requested;
                await ApplyAsync(request.Analysis, request.Directory);
                _applied = (request.Analysis, request.Directory, GetPageKey(request.Directory));
            }

            ErrorBar.IsOpen = false;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException or TimeoutException)
        {
            // Typically the WebView2 runtime is missing. Editing keeps working without a preview.
            LoadingRing.IsActive = false;
            ErrorBar.IsOpen = true;
            _environment?.Report(ex);
        }
        finally
        {
            _busy = false;
        }

        if (_pendingScroll is { } scroll)
        {
            ScrollTo(scroll.Line, scroll.AtEnd);
        }
    }

    /// <summary>Re-renders with the current settings and theme.</summary>
    public void Refresh()
    {
        if (_view is not null)
        {
            Show(_requested.Analysis, _requested.Directory);
        }
    }

    /// <summary>Scrolls so the block that contains a source line is at the top, as in the editor.</summary>
    public async void ScrollTo(double line, bool atEnd)
    {
        if (_core is null || _loadingPage || _busy)
        {
            _pendingScroll = (line, atEnd);
            return;
        }

        _pendingScroll = null;
        var script = atEnd
            ? "window.scrollTo(0, document.documentElement.scrollHeight);"
            : "(function(l){var c=document.getElementById('content').children,p=null,n=null;" +
              "for(var i=0;i<c.length;i++){var s=+c[i].dataset.line;if(s<=l){p=c[i];}else{n=c[i];break;}}" +
              "if(!p||l<=0){window.scrollTo(0,0);return;}" +
              "var pl=+p.dataset.line,top=p.offsetTop,bottom=n?n.offsetTop:top+p.offsetHeight,nl=n?+n.dataset.line:pl+1;" +
              "var f=nl>pl?Math.min(1,(l-pl)/(nl-pl)):0;window.scrollTo(0,Math.max(0,top+(bottom-top)*f-12));})(" +
              line.ToString("0.###", CultureInfo.InvariantCulture) + ");";
        try
        {
            await _core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
    }

    /// <summary>Releases the browser process resources of this preview.</summary>
    public void Close()
    {
        _closed = true;
        if (_view is not null)
        {
            Host.Children.Remove(_view);
            _core!.NavigationStarting -= OnNavigationStarting;
            _environment!.Release(_view);
            _view = null;
            _core = null;
        }
    }

    private string GetPageKey(string? directory) => string.Create(CultureInfo.InvariantCulture,
        $"{ActualTheme}|{_settings!.PreviewStyle}|{_settings.PreviewFontSize}|{_settings.AllowRemoteImages}|{directory is not null}");

    private async Task ApplyAsync(MarkdownAnalysis? analysis, string? directory)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var browserTime = TimeSpan.Zero;
        if (_core is null)
        {
            // Starting the browser takes from a fraction of a second to, on a busy machine, much
            // longer: the pane says so instead of staying blank.
            LoadingRing.IsActive = true;
            var view = _environment!.TakeSpare();
            if (view is not null)
            {
                // The view another preview left behind is ready at once.
                Host.Children.Insert(0, view);
                _core = view.CoreWebView2;
            }
            else
            {
                // A browser left over from a session that did not end well can make the first
                // attempts fail while it goes away, so it is tried a few times before giving up.
                for (var attempt = 1; _core is null; attempt++)
                {
                    view = new WebView2 { DefaultBackgroundColor = Colors.Transparent };
                    Host.Children.Insert(0, view);
                    try
                    {
                        _core = await _environment.InitializeAsync(view);
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                    {
                        // A control without a browser behind it is of no use.
                        Host.Children.Remove(view);
                        view.Close();
                        if (attempt == StartAttempts || _closed)
                        {
                            LoadingRing.IsActive = false;
                            throw;
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250 * attempt, 1500)));
                    }
                }
            }

            if (_closed)
            {
                Host.Children.Remove(view);
                _environment.Release(view!);
                _core = null;
                LoadingRing.IsActive = false;
                return;
            }

            _view = view;
            _core.NavigationStarting += OnNavigationStarting;
            browserTime = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }

        var core = _core;
        if (!_directoryMapped || !string.Equals(directory, _mappedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            WebViewEnvironment.MapDocumentFolder(core, directory);
            _mappedDirectory = directory;
            _directoryMapped = true;
        }

        var pageKey = GetPageKey(directory);
        if (pageKey != _pageKey)
        {
            try
            {
                await LoadShellAsync(core, directory);
            }
            finally
            {
                LoadingRing.IsActive = false;
            }

            _pageKey = pageKey;
            _shownBlocks = [];
            _environment!.ReportIfSlow(browserTime, System.Diagnostics.Stopwatch.GetElapsedTime(started) - browserTime);
        }

        // Only what differs from the blocks on screen is sent to the page.
        var blocks = analysis?.Blocks ?? [];
        if (PreviewPatch.Create(_shownBlocks, blocks) is { } patch)
        {
            await core.ExecuteScriptAsync(patch.ToScript());
        }

        _shownBlocks = blocks;
    }

    private async Task LoadShellAsync(CoreWebView2 core, string? directory)
    {
        var page = HtmlDocumentBuilder.BuildPage("", "", new HtmlPageOptions
        {
            Dark = ActualTheme == ElementTheme.Dark,
            Style = _settings!.PreviewStyle,
            FontSize = _settings.PreviewFontSize,
            AllowRemoteImages = _settings.AllowRemoteImages,
            LocalResourceOrigin = DocumentViewModel.PreviewOrigin,
            TransparentBackground = true,
            Language = _localizer!.Get("LanguageTag"),
        });

        var loaded = new TaskCompletionSource();
        void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult();
        core.NavigationCompleted += OnCompleted;
        _loadingPage = true;
        try
        {
            core.NavigateToString(page);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            _loadingPage = false;
            core.NavigationCompleted -= OnCompleted;
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The only navigation the preview performs is loading its own page. Links never navigate it:
        // web links open in the default browser, local files open as tabs.
        if (_loadingPage && !e.IsUserInitiated)
        {
            return;
        }

        // Jumping to an anchor of the page itself (footnotes, heading links) is fine.
        if (e.Uri.Split('#')[0] == sender.Source.Split('#')[0])
        {
            return;
        }

        e.Cancel = true;
        if (!e.IsUserInitiated || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (string.Equals(uri.Host, DocumentViewModel.PreviewHost, StringComparison.OrdinalIgnoreCase))
        {
            if (_mappedDirectory is not null)
            {
                var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var path = Path.GetFullPath(Path.Combine(_mappedDirectory, relative));
                if (File.Exists(path))
                {
                    LocalFileActivated?.Invoke(this, path);
                }
            }
        }
        else if (uri.Scheme is "http" or "https" or "mailto")
        {
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }
}
