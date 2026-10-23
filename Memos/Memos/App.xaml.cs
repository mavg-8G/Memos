using Memos.Core.Markdown;
using Memos.Core.Services;
using Memos.Core.ViewModels;
using Memos.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Memos;

public partial class App : Application
{
    private readonly IReadOnlyList<string> _startupPaths;
    private MainWindow? _window;

    public App(IReadOnlyList<string> startupPaths)
    {
        _startupPaths = startupPaths;
        Services = ConfigureServices();

        // The language override has to be in place before any resource is looked up.
        var language = Services.GetRequiredService<ISettingsService>().Current.Language;
        if (!string.IsNullOrEmpty(language))
        {
            try
            {
                Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // An unknown tag in the settings file: keep following Windows.
            }
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // Failures outside the UI thread are recorded too.
        var log = Services.GetRequiredService<IErrorLog>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                log.Write(exception, "AppDomain");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write(e.Exception, "Task");
            e.SetObserved();
        };
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services { get; }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        Program.FilesReceived += paths => _window.DispatcherQueue.TryEnqueue(() => _window.OpenFromAnotherInstance(paths));
        _window.Activate();
        await _window.ViewModel.InitializeAsync(_startupPaths);

        // The first Markdown analysis of a session is many times slower than the rest. It is paid
        // here, in the background, instead of when the first document or preview is opened.
        _ = Task.Run(MarkdownService.WarmUp);
    }

    private static ServiceProvider ConfigureServices()
    {
        // MEMOS_DATA_DIR redirects settings and session data, for portable use and for testing.
        var dataDirectory = Environment.GetEnvironmentVariable("MEMOS_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Memos");

        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new AppPaths(dataDirectory));
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<IRecentFilesService, RecentFilesService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<ISearchService, SearchService>();
        services.AddSingleton<IMarkdownService, MarkdownService>();
        services.AddSingleton<ILocalizer, Localizer>();
        services.AddSingleton<IErrorLog, ErrorLog>();

        // Services that need the window resolve it lazily through WindowContext.
        services.AddSingleton<WindowContext>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IPdfExporter, NativePdfExporter>();
        services.AddSingleton<IPrintService, WindowsPrintService>();
        services.AddSingleton<ThemeService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Keep a local trace for diagnosis; nothing ever leaves the machine. It can be read,
        // copied or deleted from Settings.
        Services.GetRequiredService<IErrorLog>().Write(e.Exception, "UI");
    }
}
