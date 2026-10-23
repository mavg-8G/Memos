using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Memos.UITests;

/// <summary>
/// A running Memos window for one test. Each instance has its own data folder, so tests never see
/// each other's settings, never touch the user's, and never merge into a Memos the user has open.
/// The interface is forced to English so element names are stable.
/// </summary>
public sealed partial class MemosApp : IDisposable
{
    /// <summary>
    /// English interface, and Markdown documents open without the preview: its rendered text adds
    /// many elements to the automation tree, so tests that are not about the preview keep it closed.
    /// </summary>
    public const string DefaultSettings =
        """{ "Language": "en-US", "MarkdownViewMode": "Editor", "WindowWidth": 1200, "WindowHeight": 760 }""";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly Process _process;
    private readonly Dictionary<string, string> _environment;

    private MemosApp(string root, Process process, Dictionary<string, string> environment)
    {
        Root = root;
        _process = process;
        _environment = environment;
        Window = WaitFor(() =>
        {
            _process.Refresh();
            return _process.HasExited || _process.MainWindowHandle == 0 ? null : AutomationElement.FromHandle(_process.MainWindowHandle);
        }, "the main window");

        // The window exists before its content does: wait for the first tab, or for a dialog shown
        // during start-up (which holds the first tab back until it is answered).
        WaitFor(() => Window.FindFirst(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton"))), "the first tab");

        // Keyboard focus moves into the editor one dispatcher turn after the tab appears.
        Thread.Sleep(700);
    }

    /// <summary>Temporary folder of this test: <c>data</c> for the app, <c>docs</c> for test files.</summary>
    public string Root { get; }

    public string DataDirectory => Path.Combine(Root, "data");

    public AutomationElement Window { get; }

    public bool HasExited
    {
        get
        {
            _process.Refresh();
            return _process.HasExited;
        }
    }

    /// <summary>Path of a file in the test's own folder (it does not have to exist).</summary>
    public string Doc(string name) => Path.Combine(Root, "docs", name);

    // ---- Launching ----------------------------------------------------------------------------------

    /// <summary>Starts the app. <paramref name="setup"/> can create files and settings before it runs.</summary>
    public static MemosApp Launch(Func<string, IEnumerable<string>>? setup = null, string? settingsJson = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "MemosUITests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "data", "settings.json"),
            settingsJson ?? DefaultSettings);

        var arguments = setup?.Invoke(Path.Combine(root, "docs")) ?? [];
        var environment = new Dictionary<string, string> { ["MEMOS_DATA_DIR"] = Path.Combine(root, "data") };
        return new MemosApp(root, Start(arguments, environment), environment);
    }

    /// <summary>Starts a second process with the same data folder, as double-clicking a file would.</summary>
    public void LaunchAgain(params string[] arguments)
    {
        using var second = Start(arguments, _environment);
        Assert.True(second.WaitForExit(TimeSpan.FromSeconds(20)), "The second launch should hand over its files and exit.");
    }

    public static string WriteDoc(string docsFolder, string name, string content)
    {
        var path = Path.Combine(docsFolder, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static Process Start(IEnumerable<string> arguments, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(FindExecutable()) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        return Process.Start(info) ?? throw new InvalidOperationException("Memos.exe could not be started.");
    }

    /// <summary>
    /// The unpackaged build of the app: MEMOS_EXE if set, otherwise the newest one under Memos\bin.
    /// </summary>
    private static string FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("MEMOS_EXE") is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? configured : throw new FileNotFoundException("MEMOS_EXE points to a file that does not exist.", configured);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var bin = Path.Combine(directory.FullName, "Memos", "bin");
            if (!Directory.Exists(bin))
            {
                continue;
            }

            // Only the unpackaged build can run outside its MSIX package; it is the one that carries
            // the Windows App SDK runtime next to the executable.
            var candidate = Directory.EnumerateFiles(bin, "Memos.exe", SearchOption.AllDirectories)
                .Where(exe => File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Microsoft.WindowsAppRuntime.dll")))
                .OrderByDescending(exe => File.GetLastWriteTimeUtc(Path.ChangeExtension(exe, ".dll")))
                .FirstOrDefault();
            if (candidate is not null)
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "No unpackaged build of Memos was found. Build it first with: " +
            "dotnet build Memos/Memos/Memos.csproj -c Debug -p:Platform=x64 -p:MemosUnpackaged=true");
    }

    // ---- Finding elements ---------------------------------------------------------------------------

    /// <summary>Element with the given AutomationId (the x:Name in XAML), waiting until it appears.</summary>
    public AutomationElement Find(string automationId) =>
        WaitFor(() => TryFind(automationId), $"the element '{automationId}'");

    public AutomationElement? TryFind(string automationId) =>
        Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public AutomationElement FindByName(string name, ControlType? type = null) =>
        WaitFor(() => TryFindByName(name, type), $"the element named '{name}'");

    public AutomationElement? TryFindByName(string name, ControlType? type = null) =>
        Window.FindFirst(TreeScope.Descendants, NameCondition(name, type));

    /// <summary>Flyouts and menus live in their own popup windows, so they are searched process-wide.</summary>
    public AutomationElement FindInPopups(string name, ControlType? type = null) =>
        WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id), NameCondition(name, type))), $"the popup item '{name}'");

    public string[] TabNames() => Window
        .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
        .Cast<AutomationElement>().Select(e => e.Current.Name).ToArray();

    public string Text(string automationId) => Find(automationId).Current.Name;

    private static Condition NameCondition(string name, ControlType? type)
    {
        var byName = new PropertyCondition(AutomationElement.NameProperty, name);
        return type is null ? byName : new AndCondition(byName, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
    }

    // ---- Acting -------------------------------------------------------------------------------------

    public static void Invoke(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
        }
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
        }
        else if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
        }
        else if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            ((ExpandCollapsePattern)expand).Expand();
        }
        else
        {
            throw new InvalidOperationException($"'{element.Current.Name}' cannot be invoked.");
        }
    }

    public void Click(string automationId) => Invoke(Find(automationId));

    public void SetText(string automationId, string value) =>
        ((ValuePattern)Find(automationId).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    /// <summary>Opens a menu of the menu bar and walks down to an item, e.g. Menu("Edit", "Lines", "Duplicate line").</summary>
    public void Menu(params string[] path)
    {
        Invoke(FindByName(path[0], ControlType.MenuItem));
        foreach (var item in path.Skip(1))
        {
            Invoke(FindInPopups(item, ControlType.MenuItem));
        }
    }

    public void SelectTab(string name) => Invoke(FindByName(name, ControlType.TabItem));

    /// <summary>
    /// Puts the keyboard focus in the text editor of the active tab. A window started by a test
    /// runner is not always given the foreground by Windows, and without this its editor may never
    /// have received the focus.
    /// </summary>
    public void FocusEditor()
    {
        var editor = FindByName("Text editor", ControlType.Edit);
        if (editor.Current.HasKeyboardFocus)
        {
            return;
        }

        try
        {
            editor.SetFocus();
        }
        catch (InvalidOperationException)
        {
            // Windows refused to change the foreground window; the control keeps the focus it had.
        }

        // Give the focus change time to settle, or the first key could go to the previous control.
        var watch = Stopwatch.StartNew();
        while (!editor.Current.HasKeyboardFocus && watch.ElapsedMilliseconds < 1500)
        {
            Thread.Sleep(50);
        }

        Thread.Sleep(200);
    }

    /// <summary>Types text into the editor of the active tab.</summary>
    public void Type(string text)
    {
        FocusEditor();
        foreach (var c in text)
        {
            PostMessageW(InputWindow(), 0x0102 /* WM_CHAR */, c, 1);
            Thread.Sleep(40);
        }
    }

    /// <summary>Presses and releases a key without modifiers (virtual-key code) in the editor.</summary>
    public void Press(int virtualKey)
    {
        FocusEditor();
        // Navigation keys (Insert, Delete, Home, arrows…) are "extended" keys; without that flag
        // Windows reports them as their numeric-keypad twins.
        var extended = virtualKey is (>= 0x21 and <= 0x28) or 0x2D or 0x2E ? 1 << 24 : 0;
        var target = InputWindow();
        PostMessageW(target, 0x0100 /* WM_KEYDOWN */, virtualKey, 1 | extended);
        PostMessageW(target, 0x0101 /* WM_KEYUP */, virtualKey, unchecked((nint)(0xC0000001 | (uint)extended)));
        Thread.Sleep(150);
    }

    /// <summary>Visible top-level windows of the app other than the main one.</summary>
    public nint[] OwnWindows()
    {
        var found = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == (uint)_process.Id && window != _process.MainWindowHandle && IsWindowVisible(window) && Title(window).Length > 0)
            {
                found.Add(window);
            }

            return true;
        }, 0);
        return [.. found];
    }

    /// <summary>
    /// Print dialogs of Windows open for a document. The dialog belongs to the system, not to the
    /// app's process, and is titled with the name of the print job followed by a localized word.
    /// </summary>
    public nint[] PrintDialogs(string documentName)
    {
        var found = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId != (uint)_process.Id && IsWindowVisible(window) &&
                ClassName(window) == "ApplicationFrameWindow" && Title(window).StartsWith(documentName + " - ", StringComparison.Ordinal))
            {
                found.Add(window);
            }

            return true;
        }, 0);
        return [.. found];
    }

    /// <summary>Asks any window to close, as its X button does.</summary>
    public static void CloseWindow(nint window) => PostMessageW(window, 0x0010 /* WM_CLOSE */, 0, 0);

    /// <summary>Asks the window to close, as the X button does.</summary>
    public void RequestClose() => _process.CloseMainWindow();

    public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

    // ---- Waiting ------------------------------------------------------------------------------------

    public static T WaitFor<T>(Func<T?> probe, string what, TimeSpan? timeout = null)
        where T : class
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (probe() is { } found)
                {
                    return found;
                }
            }
            catch (ElementNotAvailableException)
            {
                // The tree changed while it was being read; try again.
            }

            if (watch.Elapsed > (timeout ?? DefaultTimeout))
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>Waits until a condition about the window holds.</summary>
    public static void Eventually(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        WaitFor(() => condition() ? "ok" : null, what, timeout);

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file can stay locked for a moment after the app exits; the temp folder is cleaned by the OS.
        }
    }

    // ---- Keyboard plumbing --------------------------------------------------------------------------

    /// <summary>
    /// The child window WinUI reads keyboard input from. Messages are posted straight to it, so the
    /// tests do not need the window to be in the foreground and cannot type into another program.
    /// </summary>
    private nint InputWindow()
    {
        nint found = 0;
        EnumChildWindows(_process.MainWindowHandle, (window, _) =>
        {
            if (ClassName(window) == "InputSiteWindowClass")
            {
                found = window;
            }

            return true;
        }, 0);
        return found != 0 ? found : throw new InvalidOperationException("The input window of Memos was not found.");
    }

    private static string ClassName(nint window)
    {
        var name = new char[64];
        return new string(name, 0, GetClassNameW(window, name, name.Length));
    }

    private static string Title(nint window)
    {
        var title = new char[260];
        return new string(title, 0, GetWindowTextW(window, title, title.Length));
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, [Out] char[] name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, [Out] char[] title, int capacity);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
