using System.ComponentModel;
using Memos.Core.Markdown;
using Memos.Core.Models;
using Memos.Core.Services;
using Memos.Core.ViewModels;
using Memos.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Memos.Views;

/// <summary>Everything shown for one tab: notices, the find bar, the editor and the Markdown panes.</summary>
public sealed partial class DocumentView : UserControl
{
    private readonly AppSettings _settings;
    private MarkdownAnalysis? _styledAnalysis;
    private double _editorShare = 0.5;
    private bool _draggingSplitter;
    private bool _scrollSyncQueued;
    private bool _closed;

    public DocumentView(DocumentViewModel document, AppSettings settings, WebViewEnvironment environment, ILocalizer localizer)
    {
        Document = document;
        _settings = settings;
        InitializeComponent();

        Preview.Configure(environment, settings, localizer);
        Preview.LocalFileActivated += (_, path) => LocalFileActivated?.Invoke(this, path);
        Editor.Attach(document, settings);
        Editor.ViewChanged += (_, _) => QueueScrollSync();

        document.PropertyChanged += OnDocumentPropertyChanged;
        document.AnalysisChanged += OnAnalysisChanged;
        document.Find.MatchesChanged += OnMatchesChanged;
        document.Find.FocusRequested += OnFindFocusRequested;
        settings.PropertyChanged += OnSettingsChanged;

        UpdateNotice();
        UpdateLayoutMode();
        UpdateOutlineHint();
        if (document.Analysis is { } analysis)
        {
            ApplyAnalysis(analysis, null);
        }
    }

    public DocumentViewModel Document { get; }

    /// <summary>A link in the preview points at a local file that should open as a tab.</summary>
    public event EventHandler<string>? LocalFileActivated;

    public void FocusEditor() => ((IEditorSurface)Editor).Focus();

    /// <summary>Unhooks from the document and releases the preview's browser resources.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Document.PropertyChanged -= OnDocumentPropertyChanged;
        Document.AnalysisChanged -= OnAnalysisChanged;
        Document.Find.MatchesChanged -= OnMatchesChanged;
        Document.Find.FocusRequested -= OnFindFocusRequested;
        _settings.PropertyChanged -= OnSettingsChanged;
        Editor.Detach();
        Preview.Close();
    }

    private bool IsPreviewVisible => Document.IsMarkdown && Document.ViewMode != MarkdownViewMode.Editor;

    private bool IsEditorVisible => !Document.IsMarkdown || Document.ViewMode != MarkdownViewMode.Preview;

    // ---- Document events ------------------------------------------------------------------------------

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.Notice):
                UpdateNotice();
                break;
            case nameof(DocumentViewModel.ViewMode):
            case nameof(DocumentViewModel.Kind):
            case nameof(DocumentViewModel.IsOutlineVisible):
                UpdateLayoutMode();
                break;
            case nameof(DocumentViewModel.Headings):
                UpdateOutlineHint();
                break;
            case nameof(DocumentViewModel.FilePath):
                ShowPreview();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.PreviewStyle) or nameof(AppSettings.PreviewFontSize) or nameof(AppSettings.AllowRemoteImages))
        {
            if (IsPreviewVisible)
            {
                Preview.Refresh();
            }
        }
    }

    private void OnAnalysisChanged(object? sender, AnalysisChangedEventArgs e) =>
        ApplyAnalysis(e.Analysis, ReferenceEquals(e.Previous, _styledAnalysis) ? e.DirtyRange : null);

    private void ApplyAnalysis(MarkdownAnalysis? analysis, (int Start, int End)? dirtyRange)
    {
        // The dirty range is relative to the analysis the editor was last styled with; with any
        // other starting point everything is restyled.
        Editor.ApplySyntaxRuns(analysis?.Runs ?? [], _styledAnalysis is null ? null : dirtyRange);
        _styledAnalysis = analysis;
        ShowPreview();
    }

    private void ShowPreview()
    {
        if (IsPreviewVisible)
        {
            Preview.Show(Document.Analysis, Document.Directory);
        }
    }

    private void OnMatchesChanged(object? sender, EventArgs e) =>
        Editor.ApplySearchHighlights(Document.Find.IsVisible ? Document.Find.Matches : []);

    private void OnFindFocusRequested(object? sender, EventArgs e)
    {
        // The bar becomes visible through a binding; focus once layout has caught up.
        DispatcherQueue.TryEnqueue(() =>
        {
            QueryBox.Focus(FocusState.Programmatic);
            QueryBox.SelectAll();
        });
    }

    // ---- Layout ---------------------------------------------------------------------------------------

    private void UpdateLayoutMode()
    {
        var editor = IsEditorVisible;
        var preview = IsPreviewVisible;

        Editor.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = editor && preview ? Visibility.Visible : Visibility.Collapsed;
        Outline.Visibility = Document.IsMarkdown && Document.IsOutlineVisible ? Visibility.Visible : Visibility.Collapsed;

        EditorColumn.Width = editor ? new GridLength(preview ? _editorShare : 1, GridUnitType.Star) : new GridLength(0);
        PreviewColumn.Width = preview ? new GridLength(editor ? 1 - _editorShare : 1, GridUnitType.Star) : new GridLength(0);

        ShowPreview();
        if (preview && editor)
        {
            QueueScrollSync();
        }
    }

    private void UpdateOutlineHint() =>
        OutlineEmpty.Visibility = Document.Headings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateNotice()
    {
        if (Document.Notice is not { } notice)
        {
            NoticeBar.IsOpen = false;
            return;
        }

        NoticeBar.Message = notice.Message;
        NoticeBar.Severity = notice.Severity switch
        {
            NoticeSeverity.Warning => InfoBarSeverity.Warning,
            NoticeSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

        if (notice is { ActionLabel: { } label, Action: { } action })
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) =>
            {
                Document.Notice = null;
                await action();
            };
            NoticeBar.ActionButton = button;
        }
        else
        {
            NoticeBar.ActionButton = null;
        }

        NoticeBar.IsOpen = true;
    }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            Document.Notice = null;
        }
    }

    // ---- Scroll sync and outline ----------------------------------------------------------------------

    private void QueueScrollSync()
    {
        if (_scrollSyncQueued || !_settings.SyncScroll || !IsPreviewVisible || !IsEditorVisible)
        {
            return;
        }

        // Coalesce bursts of scroll events into one update per frame.
        _scrollSyncQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollSyncQueued = false;
            if (!_closed && IsPreviewVisible && IsEditorVisible)
            {
                Preview.ScrollTo(Editor.GetFirstVisibleLine(), Editor.IsScrolledToEnd);
            }
        });
    }

    private void OnOutlineItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not HeadingInfo heading)
        {
            return;
        }

        Document.GoToLine(heading.Line + 1);
        if (IsEditorVisible)
        {
            FocusEditor();
        }

        if (IsPreviewVisible)
        {
            Preview.ScrollTo(heading.Line, atEnd: false);
        }
    }

    // ---- Find bar -------------------------------------------------------------------------------------

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                Document.Find.FindPrevious();
            }
            else
            {
                Document.Find.FindNext();
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Document.Find.Close();
            e.Handled = true;
        }
    }

    private async void OnReplaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await Document.Find.ReplaceCurrentAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Document.Find.Close();
            e.Handled = true;
        }
    }

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => Document.Find.FindPrevious();

    private void OnFindNextClick(object sender, RoutedEventArgs e) => Document.Find.FindNext();

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => Document.Find.Close();

    private async void OnReplaceClick(object sender, RoutedEventArgs e) => await Document.Find.ReplaceCurrentAsync();

    private async void OnReplaceAllClick(object sender, RoutedEventArgs e) => await Document.Find.ReplaceAllAsync();

    // ---- Splitter -------------------------------------------------------------------------------------

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = Splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter)
        {
            return;
        }

        var available = EditorColumn.ActualWidth + PreviewColumn.ActualWidth;
        if (available <= 0)
        {
            return;
        }

        var x = e.GetCurrentPoint(Body).Position.X;
        _editorShare = Math.Clamp(x / available, 0.15, 0.85);
        EditorColumn.Width = new GridLength(_editorShare, GridUnitType.Star);
        PreviewColumn.Width = new GridLength(1 - _editorShare, GridUnitType.Star);
    }

    private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = false;
        Splitter.ReleasePointerCapture(e.Pointer);
    }

    private void OnSplitterCaptureLost(object sender, PointerRoutedEventArgs e) => _draggingSplitter = false;
}
