using Memos.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Memos.Views;

/// <summary>Panel for <see cref="GlobalSearchViewModel"/>: search and replace in all open tabs.</summary>
public sealed partial class GlobalSearchPanel : UserControl
{
    public GlobalSearchPanel(GlobalSearchViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        viewModel.FocusRequested += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            GlobalQueryBox.Focus(FocusState.Programmatic);
            GlobalQueryBox.SelectAll();
        });
    }

    public GlobalSearchViewModel ViewModel { get; }

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ViewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GlobalSearchResult result)
        {
            ViewModel.Open(result);
        }
    }
}
