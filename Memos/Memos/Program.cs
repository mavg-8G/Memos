using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Memos;

/// <summary>
/// Entry point. Only one Memos process owns the window: later launches (double-clicking a file,
/// "Open with") pass their files to it and exit, so everything opens as tabs of the same window.
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "Memos.MainInstance";

    /// <summary>Raised in the main instance when another launch hands over files. Not on the UI thread.</summary>
    public static event Action<IReadOnlyList<string>>? FilesReceived;

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            RedirectTo(main, activation);
            return 0;
        }

        main.Activated += (_, args) => FilesReceived?.Invoke(GetPaths(args, isRedirected: true));
        var startupPaths = GetPaths(activation, isRedirected: false);

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(startupPaths);
        });
        return 0;
    }

    private static void RedirectTo(AppInstance main, AppActivationArguments activation)
    {
        // The redirection is asynchronous and this is an STA thread with no message loop yet.
        using var done = new ManualResetEventSlim();
        Task.Run(() =>
        {
            try
            {
                main.RedirectActivationToAsync(activation).AsTask().Wait();
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(TimeSpan.FromSeconds(10));

        // Only the process the user just started is allowed to bring a window to the foreground.
        try
        {
            using var process = Process.GetProcessById((int)main.ProcessId);
            if (process.MainWindowHandle != 0)
            {
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
    }

    /// <summary>Files named by an activation: a file association, or arguments on the command line.</summary>
    private static IReadOnlyList<string> GetPaths(AppActivationArguments activation, bool isRedirected)
    {
        switch (activation.Kind)
        {
            case ExtendedActivationKind.File when activation.Data is IFileActivatedEventArgs file:
                return file.Files.OfType<IStorageItem>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();

            case ExtendedActivationKind.Launch when isRedirected && activation.Data is ILaunchActivatedEventArgs launch:
                var parsed = SplitCommandLine(launch.Arguments);
                // Depending on how the process was started the executable itself may come first.
                return parsed.Where((arg, i) => !(i == 0 && arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))).ToList();

            case ExtendedActivationKind.Launch:
                return Environment.GetCommandLineArgs().Skip(1).ToList();

            default:
                return [];
        }
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == 0)
        {
            return result;
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * nint.Size)) is { Length: > 0 } arg)
                {
                    result.Add(arg);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(argv);
        }

        return result;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgvW(string commandLine, out int argumentCount);
}
