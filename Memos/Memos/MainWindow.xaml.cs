using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;
using Memos.Services;
using Memos.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.Specialized;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Memos;

/// <summary>
/// The application window. It wires view-model state to controls that cannot be bound directly
/// (tabs, per-document views, dynamic menus, keyboard shortcuts); the behaviour itself is in
/// <see cref="MainViewModel"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<DocumentViewModel, DocumentView> _views = [];
    private readonly ISettingsService _settingsService;
    private readonly WebViewEnvironment _webViewEnvironment;
    private readonly ILocalizer _loc;
    private readonly WindowContext _context;
    private DocumentViewModel? _observedDocument;
    private bool _observedRichText;
    private SettingsPage? _settingsPage;
    private bool _draggingTab;
    private bool _syncingSelection;
    private bool _closeConfirmed;
    private bool _closing;

    public MainWindow(
        MainViewModel viewModel, ISettingsService settingsService, WindowContext context, ThemeService theme,
        WebViewEnvironment webViewEnvironment, ILocalizer localizer)
    {
        ViewModel = viewModel;
        _settingsService = settingsService;
        _webViewEnvironment = webViewEnvironment;
        _loc = localizer;
        _context = context;
        InitializeComponent();

        context.Window = this;
        context.HiddenHost = HiddenHost;
        theme.Apply();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Memos.ico"));
        RestoreWindowPlacement();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Documents.CollectionChanged += OnDocumentsChanged;
        ViewModel.RecentFiles.CollectionChanged += (_, _) => BuildRecentMenu();
        ViewModel.Settings.PropertyChanged += OnSettingsChanged;
        ViewModel.CloseWindowRequested += (_, _) =>
        {
            _closeConfirmed = true;
            Close();
        };

        AppWindow.Closing += OnWindowClosing;
        Activated += OnActivated;

        GlobalSearchHost.Children.Add(new GlobalSearchPanel(ViewModel.GlobalSearch));
        BuildHeadingMenus();
        BuildRecentMenu();
        OnActiveDocumentChanged();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Files handed over by a second launch of the app.</summary>
    public async void OpenFromAnotherInstance(IReadOnlyList<string> paths)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        if (paths.Count > 0)
        {
            ViewModel.IsSettingsOpen = false;
            await ViewModel.OpenPathsAsync(paths);
        }
    }

    // ---- Window lifetime ------------------------------------------------------------------------------

    private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            ViewModel.CheckExternalChanges();
        }
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        // Closing is decided asynchronously (unsaved changes), so cancel now and close again later.
        args.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (!await ViewModel.PrepareToCloseAsync())
            {
                return;
            }

            SaveWindowPlacement();
            await _settingsService.FlushAsync();
            foreach (var view in _views.Values)
            {
                view.Close();
            }

            _webViewEnvironment.Shutdown();
            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _closing = false;
        }
    }

    private void RestoreWindowPlacement()
    {
        var settings = ViewModel.Settings;
        if (settings is { WindowWidth: >= 480, WindowHeight: >= 320 })
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(settings.WindowWidth, settings.WindowHeight));
        }

        if (settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void SaveWindowPlacement()
    {
        var settings = ViewModel.Settings;
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (!maximized && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            settings.WindowWidth = AppWindow.Size.Width;
            settings.WindowHeight = AppWindow.Size.Height;
        }

        settings.WindowMaximized = maximized;
        (_settingsService as JsonSettingsService)?.MarkDirty();
    }

    // ---- View-model state → controls ------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ActiveDocument):
                OnActiveDocumentChanged();
                break;
            case nameof(MainViewModel.IsSettingsOpen):
                UpdateSettingsPage();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ShowRecentFiles))
        {
            BuildRecentMenu();
        }
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Views of closed tabs are released; views of new tabs are created when first shown.
        foreach (var removed in _views.Keys.Where(d => !ViewModel.Documents.Contains(d)).ToList())
        {
            var view = _views[removed];
            _views.Remove(removed);
            DocumentHost.Children.Remove(view);
            view.Close();
        }
    }

    private void OnActiveDocumentChanged()
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= OnActiveDocumentPropertyChanged;
        }

        var document = _observedDocument = ViewModel.ActiveDocument;
        if (document is null)
        {
            return;
        }

        document.PropertyChanged += OnActiveDocumentPropertyChanged;
        _observedRichText = document.IsRichText;
        if (!_views.TryGetValue(document, out var active))
        {
            active = new DocumentView(document, ViewModel.Settings, _webViewEnvironment, _loc);
            active.LocalFileActivated += async (_, path) => await ViewModel.OpenPathsAsync([path]);
            _views[document] = active;
            DocumentHost.Children.Add(active);
        }

        foreach (var view in _views.Values)
        {
            view.Visibility = ReferenceEquals(view, active) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!_draggingTab && !ReferenceEquals(Tabs.SelectedItem, document))
        {
            _syncingSelection = true;
            Tabs.SelectedItem = document;
            _syncingSelection = false;
        }

        UpdateDocumentChrome();
        if (!ViewModel.IsSettingsOpen)
        {
            // Wait for the view to be laid out before moving focus into it.
            DispatcherQueue.TryEnqueue(active.FocusEditor);
        }
    }

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.Kind) && _observedDocument is { } document)
        {
            var wasRichText = _observedRichText;
            _observedRichText = document.IsRichText;
            if (wasRichText && !document.IsRichText)
            {
                ReplaceView(document);
                return;
            }
        }

        if (e.PropertyName is nameof(DocumentViewModel.Kind) or nameof(DocumentViewModel.ViewMode)
            or nameof(DocumentViewModel.IsOutlineVisible) or nameof(DocumentViewModel.DisplayName))
        {
            UpdateDocumentChrome();
        }
    }

    /// <summary>
    /// A document that stops being rich text gets a new editor. Loading RTF replaces the defaults
    /// of the control (fonts, the "automatic" colour, paragraph format) and nothing puts back the
    /// ones a plain-text editor starts with.
    /// </summary>
    private void ReplaceView(DocumentViewModel document)
    {
        if (_views.Remove(document, out var view))
        {
            DocumentHost.Children.Remove(view);
            view.Close();
        }

        OnActiveDocumentChanged();
    }

    /// <summary>Title, workspace switch and view-mode toggles for the active document.</summary>
    private void UpdateDocumentChrome()
    {
        if (ViewModel.ActiveDocument is not { } document)
        {
            return;
        }

        Title = $"{document.DisplayName} - Memos";

        _syncingSelection = true;
        WorkspaceSelector.SelectedItem = document.IsMarkdown ? MarkdownWorkspaceItem : TextWorkspaceItem;
        _syncingSelection = false;

        FormattingButton.IsChecked = document.IsRichText;
        FormattingMenuItem.IsChecked = document.IsRichText;

        ViewEditorButton.IsChecked = document.ViewMode == MarkdownViewMode.Editor;
        ViewSplitButton.IsChecked = document.ViewMode == MarkdownViewMode.Split;
        ViewPreviewButton.IsChecked = document.ViewMode == MarkdownViewMode.Preview;
        OutlineButton.IsChecked = document.IsOutlineVisible;
    }

    private void UpdateSettingsPage()
    {
        if (ViewModel.IsSettingsOpen)
        {
            if (_settingsPage is null)
            {
                _settingsPage = new SettingsPage(ViewModel, _loc);
                SettingsHost.Children.Add(_settingsPage);
            }

            SettingsHost.Visibility = Visibility.Visible;
            _settingsPage.FocusFirst();
        }
        else
        {
            SettingsHost.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Tabs -----------------------------------------------------------------------------------------

    private void OnAddTabClick(TabView sender, object args) => ViewModel.NewTabCommand.Execute(null);

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is DocumentViewModel document)
        {
            await ViewModel.CloseDocumentAsync(document);
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // While a tab is dragged the list removes and re-inserts it, which would look like a switch.
        if (_syncingSelection || _draggingTab)
        {
            return;
        }

        if (Tabs.SelectedItem is DocumentViewModel document)
        {
            ViewModel.IsSettingsOpen = false;
            ViewModel.ActiveDocument = document;
        }
    }

    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args) => _draggingTab = true;

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        _draggingTab = false;
        if (args.Item is DocumentViewModel document && ViewModel.Documents.Contains(document))
        {
            ViewModel.ActiveDocument = document;
            _syncingSelection = true;
            Tabs.SelectedItem = document;
            _syncingSelection = false;
        }
    }

    private static DocumentViewModel? TabOf(object sender) => (sender as FrameworkElement)?.Tag as DocumentViewModel;

    private async void OnTabMenuCloseClick(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } document)
        {
            await ViewModel.CloseDocumentAsync(document);
        }
    }

    private void OnTabMenuCloseOthersClick(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } document)
        {
            ViewModel.CloseOthersCommand.Execute(document);
        }
    }

    private void OnTabMenuCloseRightClick(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } document)
        {
            ViewModel.CloseToRightCommand.Execute(document);
        }
    }

    private void OnTabMenuCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { FilePath: { } path })
        {
            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
        }
    }

    private void OnTabMenuRevealClick(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { FilePath: { } path } && File.Exists(path))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }

    // ---- Menus built in code --------------------------------------------------------------------------

    private void BuildHeadingMenus()
    {
        foreach (var items in new[] { HeadingMenu.Items, HeadingFlyout.Items })
        {
            for (var level = 1; level <= 6; level++)
            {
                items.Add(new MenuFlyoutItem
                {
                    Text = _loc.Format("Cmd_HeadingLevel", level),
                    Command = ViewModel.HeadingCommand,
                    CommandParameter = level.ToString(),
                });
            }

            items.Add(new MenuFlyoutSeparator());
            items.Add(new MenuFlyoutItem { Text = _loc.Get("Cmd_HeadingNone"), Command = ViewModel.HeadingCommand, CommandParameter = "0" });
        }
    }

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        RecentMenu.Visibility = ViewModel.Settings.ShowRecentFiles ? Visibility.Visible : Visibility.Collapsed;
        foreach (var path in ViewModel.RecentFiles)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path), Command = ViewModel.OpenRecentCommand, CommandParameter = path };
            ToolTipService.SetToolTip(item, path);
            RecentMenu.Items.Add(item);
        }

        if (ViewModel.RecentFiles.Count == 0)
        {
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = _loc.Get("Recent_Empty"), IsEnabled = false });
        }
        else
        {
            RecentMenu.Items.Add(new MenuFlyoutSeparator());
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = _loc.Get("Recent_Clear"), Command = ViewModel.ClearRecentCommand });
        }
    }

    private void OnLineEndingFlyoutOpening(object? sender, object e)
    {
        LineEndingFlyout.Items.Clear();
        foreach (var lineEnding in Enum.GetValues<LineEnding>())
        {
            LineEndingFlyout.Items.Add(new RadioMenuFlyoutItem
            {
                Text = _loc.Get($"LineEnding_{lineEnding}"),
                GroupName = "LineEnding",
                IsChecked = ViewModel.ActiveDocument?.LineEnding == lineEnding,
                Command = ViewModel.SetLineEndingCommand,
                CommandParameter = lineEnding,
            });
        }
    }

    private void OnEncodingFlyoutOpening(object? sender, object e)
    {
        EncodingFlyout.Items.Clear();
        var save = new MenuFlyoutSubItem { Text = _loc.Get("Encoding_SaveWith") };
        var reopen = new MenuFlyoutSubItem { Text = _loc.Get("Encoding_ReopenWith"), IsEnabled = ViewModel.HasFile };
        foreach (var encoding in TextEncodings.All)
        {
            save.Items.Add(new RadioMenuFlyoutItem
            {
                Text = TextEncodings.GetDisplayName(encoding),
                GroupName = "Encoding",
                IsChecked = ViewModel.ActiveDocument?.Encoding == encoding,
                Command = ViewModel.SetEncodingCommand,
                CommandParameter = encoding,
            });
            reopen.Items.Add(new MenuFlyoutItem
            {
                Text = TextEncodings.GetDisplayName(encoding),
                Command = ViewModel.ReopenWithEncodingCommand,
                CommandParameter = encoding,
            });
        }

        EncodingFlyout.Items.Add(save);
        EncodingFlyout.Items.Add(reopen);
    }

    // ---- Toolbar handlers -----------------------------------------------------------------------------

    private async void OnWorkspaceSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_syncingSelection)
        {
            var kind = ReferenceEquals(sender.SelectedItem, MarkdownWorkspaceItem) ? DocumentKind.Markdown : DocumentKind.Text;
            await ViewModel.SetKindCommand.ExecuteAsync(kind);

            // Leaving rich text can be cancelled; the switch then goes back to where it was.
            UpdateDocumentChrome();
        }
    }

    private async void OnFormattingClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleFormattingCommand.ExecuteAsync(null);

        // The toggle flips on its own when clicked; it has to show what the document really is.
        UpdateDocumentChrome();
    }

    private void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<MarkdownViewMode>(tag, out var mode))
        {
            ViewModel.SetViewModeCommand.Execute(mode);
        }

        // Clicking the mode that is already active must not leave its toggle unchecked.
        UpdateDocumentChrome();
    }

    private void OnOutlineClick(object sender, RoutedEventArgs e) => ViewModel.ToggleOutlineCommand.Execute(null);

    private void OnInsertTableClick(object sender, RoutedEventArgs e)
    {
        TableFlyout.Hide();
        var rows = double.IsNaN(TableRowsBox.Value) ? 3 : (int)TableRowsBox.Value;
        var columns = double.IsNaN(TableColumnsBox.Value) ? 3 : (int)TableColumnsBox.Value;
        ViewModel.InsertTable(rows, columns);
    }

    private void OnInsertDefaultTableClick(object sender, RoutedEventArgs e) => ViewModel.InsertTable(3, 3);

    // ---- Drag and drop --------------------------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _loc.Get("Drop_Open");
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0)
            {
                ViewModel.IsSettingsOpen = false;
                await ViewModel.OpenPathsAsync(paths);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---- Keyboard shortcuts ---------------------------------------------------------------------------

    /// <summary>
    /// Shortcuts are handled while the key event tunnels down from the window, before the focused
    /// control sees it. That keeps them deterministic next to RichEditBox's own key handling. Keys
    /// pressed inside menus, dialogs and flyouts never reach this handler.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_context.IsDialogOpen)
        {
            return;
        }

        var control = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);

        if (e.Key == VirtualKey.Escape && ViewModel.IsSettingsOpen)
        {
            ViewModel.CloseSettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && ViewModel.ActiveDocument?.Find.IsVisible == true)
        {
            ViewModel.ActiveDocument.Find.Close();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.F3 && ViewModel.ActiveDocument is { Find.IsVisible: true } searching)
        {
            if (shift)
            {
                searching.Find.FindPrevious();
            }
            else
            {
                searching.Find.FindNext();
            }

            e.Handled = true;
            return;
        }

        // Editing shortcuts only apply while the text editor has the focus, so typing in the search
        // box or in a settings field keeps its normal behaviour.
        var inEditor = FocusManager.GetFocusedElement(Root.XamlRoot) is RichEditBox;
        var command = (control, shift, alt, e.Key) switch
        {
            (true, false, false, VirtualKey.N) => ViewModel.NewTextCommand,
            (true, true, false, VirtualKey.N) => ViewModel.NewMarkdownCommand,
            (true, false, true, VirtualKey.N) => ViewModel.NewRichTextCommand,
            (true, false, false, VirtualKey.O) => ViewModel.OpenCommand,
            (true, false, false, VirtualKey.P) => ViewModel.PrintCommand,
            (true, false, false, VirtualKey.S) => ViewModel.SaveCommand,
            (true, true, false, VirtualKey.S) => ViewModel.SaveAsCommand,
            (true, false, true, VirtualKey.S) => ViewModel.SaveAllCommand,
            (true, false, false, VirtualKey.W) => ViewModel.CloseTabCommand,
            (true, true, false, VirtualKey.T) => ViewModel.ReopenClosedCommand,
            (true, false, false, VirtualKey.Tab) => ViewModel.NextTabCommand,
            (true, true, false, VirtualKey.Tab) => ViewModel.PreviousTabCommand,
            (true, false, false, VirtualKey.F) => ViewModel.FindCommand,
            (true, true, false, VirtualKey.F) => ViewModel.FindInTabsCommand,
            (true, false, false, VirtualKey.H) => ViewModel.ReplaceCommand,
            (true, false, false, VirtualKey.G) => ViewModel.GoToLineCommand,
            (true, false, false, (VirtualKey)188) => ViewModel.OpenSettingsCommand, // Ctrl+,
            (true, _, false, (VirtualKey)187) or (true, false, false, VirtualKey.Add) => ViewModel.ZoomInCommand,
            (true, false, false, (VirtualKey)189) or (true, false, false, VirtualKey.Subtract) => ViewModel.ZoomOutCommand,
            (true, false, false, VirtualKey.Number0) or (true, false, false, VirtualKey.NumberPad0) => ViewModel.ResetZoomCommand,

            (true, false, false, VirtualKey.Z) when inEditor => ViewModel.UndoCommand,
            (true, false, false, VirtualKey.Y) when inEditor => ViewModel.RedoCommand,
            (true, true, false, VirtualKey.Z) when inEditor => ViewModel.RedoCommand,
            (true, false, false, VirtualKey.D) when inEditor => ViewModel.DuplicateLineCommand,
            (true, true, false, VirtualKey.K) when inEditor => ViewModel.DeleteLineCommand,
            (false, false, true, VirtualKey.Up) when inEditor => ViewModel.MoveLineUpCommand,
            (false, false, true, VirtualKey.Down) when inEditor => ViewModel.MoveLineDownCommand,
            (true, true, false, VirtualKey.U) when inEditor => ViewModel.UppercaseCommand,
            (true, false, false, VirtualKey.U) when inEditor && ViewModel.IsRichTextActive => ViewModel.UnderlineCommand,
            (true, false, false, VirtualKey.U) when inEditor => ViewModel.LowercaseCommand,
            (true, false, false, (VirtualKey)221) when inEditor => ViewModel.IndentCommand,  // Ctrl+]
            (true, false, false, (VirtualKey)219) when inEditor => ViewModel.OutdentCommand, // Ctrl+[
            (true, false, false, VirtualKey.B) when inEditor => ViewModel.BoldCommand,
            (true, false, false, VirtualKey.I) when inEditor => ViewModel.ItalicCommand,
            (true, false, false, VirtualKey.K) when inEditor => ViewModel.LinkCommand,
            _ => (System.Windows.Input.ICommand?)null,
        };

        if (command is null)
        {
            if ((control, shift, alt, e.Key) == (false, false, true, VirtualKey.Z))
            {
                ViewModel.Settings.WordWrap = !ViewModel.Settings.WordWrap;
                e.Handled = true;
            }

            return;
        }

        // The shortcut belongs to the app even when the command is unavailable right now (for
        // example Ctrl+B in a plain-text document), so the control underneath never reinterprets it.
        e.Handled = true;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
