using Memos.Core.Models;
using Memos.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace Memos.Services;

/// <summary>Strings from Strings/&lt;language&gt;/Resources.resw. A missing key shows up as the key itself.</summary>
public sealed class Localizer : ILocalizer
{
    private readonly ResourceLoader _loader = new();

    /// <summary>Available to XAML markup, which cannot receive constructor-injected services.</summary>
    public static Localizer? Instance { get; private set; }

    public Localizer() => Instance = this;

    public string Get(string key)
    {
        try
        {
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return key;
        }
    }

    public string Format(string key, params object?[] args) => string.Format(Get(key), args);
}

/// <summary>The main window, for services that need to parent dialogs and pickers to it.</summary>
public sealed class WindowContext
{
    public Window? Window { get; set; }

    /// <summary>An invisible panel in the window where short-lived helper controls can live.</summary>
    public Panel? HiddenHost { get; set; }

    /// <summary>True while a modal dialog is showing; the window ignores its shortcuts meanwhile.</summary>
    public bool IsDialogOpen { get; set; }

    public FrameworkElement Root => (FrameworkElement)(Window?.Content ?? throw new InvalidOperationException("The window is not ready."));

    public WindowId WindowId => Window?.AppWindow.Id ?? throw new InvalidOperationException("The window is not ready.");
}

public sealed class DialogService(WindowContext context, ILocalizer loc) : IDialogService
{
    // WinUI allows a single ContentDialog at a time; later requests wait for their turn.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task ShowMessageAsync(string title, string message) =>
        ShowAsync(new ContentDialog { Title = title, Content = WrapText(message), CloseButtonText = loc.Get("Btn_Ok"), DefaultButton = ContentDialogButton.Close });

    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, string closeText) =>
        await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = WrapText(message),
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        }) == ContentDialogResult.Primary;

    public async Task<SaveChangesChoice> ConfirmSaveChangesAsync(string documentName) =>
        await ShowAsync(new ContentDialog
        {
            Title = loc.Get("Msg_SaveChanges_Title"),
            Content = WrapText(loc.Format("Msg_SaveChanges", documentName)),
            PrimaryButtonText = loc.Get("Btn_Save"),
            SecondaryButtonText = loc.Get("Btn_DontSave"),
            CloseButtonText = loc.Get("Btn_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        }) switch
        {
            ContentDialogResult.Primary => SaveChangesChoice.Save,
            ContentDialogResult.Secondary => SaveChangesChoice.Discard,
            _ => SaveChangesChoice.Cancel,
        };

    public async Task<int?> PromptGoToLineAsync(int currentLine, int lineCount)
    {
        var box = new NumberBox
        {
            Header = loc.Format("GoTo_Header", lineCount),
            Value = currentLine,
            Minimum = 1,
            Maximum = lineCount,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 280,
        };
        var dialog = new ContentDialog
        {
            Title = loc.Get("GoTo_Title"),
            Content = box,
            PrimaryButtonText = loc.Get("GoTo_Go"),
            CloseButtonText = loc.Get("Btn_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await ShowAsync(dialog) != ContentDialogResult.Primary || double.IsNaN(box.Value))
        {
            return null;
        }

        return (int)Math.Clamp(box.Value, 1, lineCount);
    }

    public async Task<LinkInput?> PromptLinkAsync(string initialText)
    {
        var text = new TextBox { Header = loc.Get("Link_Text"), Text = initialText };
        var url = new TextBox { Header = loc.Get("Link_Url"), PlaceholderText = "https://" };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(text);
        panel.Children.Add(url);

        var dialog = new ContentDialog
        {
            Title = loc.Get("Link_Title"),
            Content = panel,
            PrimaryButtonText = loc.Get("Link_Insert"),
            CloseButtonText = loc.Get("Btn_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        url.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(url.Text);

        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        var address = url.Text.Trim();
        return new LinkInput(string.IsNullOrWhiteSpace(text.Text) ? address : text.Text, address);
    }

    private static TextBlock WrapText(string message) => new() { Text = message, TextWrapping = TextWrapping.Wrap };

    private static Task WaitUntilLoadedAsync(FrameworkElement root)
    {
        if (root.IsLoaded && root.XamlRoot is not null)
        {
            return Task.CompletedTask;
        }

        var loaded = new TaskCompletionSource();
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            root.Loaded -= OnLoaded;
            loaded.TrySetResult();
        }

        root.Loaded += OnLoaded;
        return loaded.Task;
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            // A dialog can be requested while the window is still starting (a file passed on the
            // command line that cannot be opened); it has to wait until there is a visual tree.
            await WaitUntilLoadedAsync(context.Root);
            dialog.XamlRoot = context.Root.XamlRoot;
            dialog.RequestedTheme = context.Root.ActualTheme;
            context.IsDialogOpen = true;
            return await dialog.ShowAsync();
        }
        finally
        {
            context.IsDialogOpen = false;
            _gate.Release();
        }
    }
}

public sealed class FilePickerService(WindowContext context, ILocalizer loc) : IFilePickerService
{
    private static readonly string[] TextExtensions = [".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf"];
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"];

    public async Task<IReadOnlyList<string>> PickOpenFilesAsync()
    {
        var picker = new FileOpenPicker(context.WindowId) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add(loc.Get("Filter_Supported"), [.. TextExtensions, .. MarkdownExtensions]);
        picker.FileTypeChoices.Add(loc.Get("Filter_Text"), [.. TextExtensions]);
        picker.FileTypeChoices.Add(loc.Get("Filter_Markdown"), [.. MarkdownExtensions]);
        picker.FileTypeChoices.Add(loc.Get("Filter_All"), ["*"]);

        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, DocumentKind kind, string? suggestedFolder)
    {
        var picker = new FileSavePicker(context.WindowId)
        {
            SuggestedFileName = suggestedName,
            DefaultFileExtension = kind == DocumentKind.Markdown ? ".md" : ".txt",
        };
        SetFolder(folder => picker.SuggestedFolder = folder, suggestedFolder);

        // The first choice is preselected, so it is the one that matches the workspace.
        if (kind == DocumentKind.Markdown)
        {
            picker.FileTypeChoices.Add(loc.Get("Filter_Markdown"), [.. MarkdownExtensions]);
            picker.FileTypeChoices.Add(loc.Get("Filter_Text"), [.. TextExtensions]);
        }
        else
        {
            picker.FileTypeChoices.Add(loc.Get("Filter_Text"), [.. TextExtensions]);
            picker.FileTypeChoices.Add(loc.Get("Filter_Markdown"), [.. MarkdownExtensions]);
        }

        picker.FileTypeChoices.Add(loc.Get("Filter_All"), ["."]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public async Task<string?> PickExportFileAsync(string suggestedName, ExportFormat format, string? suggestedFolder)
    {
        var extension = format == ExportFormat.Pdf ? ".pdf" : ".html";
        var picker = new FileSavePicker(context.WindowId) { SuggestedFileName = suggestedName, DefaultFileExtension = extension };
        SetFolder(folder => picker.SuggestedFolder = folder, suggestedFolder);
        picker.FileTypeChoices.Add(format == ExportFormat.Pdf ? "PDF" : "HTML", [extension]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public async Task<string?> PickImageAsync(string? suggestedFolder)
    {
        var picker = new FileOpenPicker(context.WindowId) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        SetFolder(folder => picker.SuggestedFolder = folder, suggestedFolder);
        picker.FileTypeChoices.Add(loc.Get("Filter_Images"), [.. ImageExtensions]);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private static void SetFolder(Action<string> apply, string? folder)
    {
        if (folder is not null && Directory.Exists(folder))
        {
            apply(folder);
        }
    }
}

public sealed class ClipboardService : IClipboardService
{
    public void SetHtml(string html, string plainText)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
        package.SetText(plainText);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
