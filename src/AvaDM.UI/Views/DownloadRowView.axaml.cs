using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaDM.UI.ViewModels;

namespace AvaDM.UI.Views;

public partial class DownloadRowView : UserControl
{
    public DownloadRowView()
    {
        InitializeComponent();
    }

    /// <summary>Opens the completed download on a double-click anywhere in the row - except on one
    /// of its own icon buttons, which already have their own click behavior.</summary>
    private void OnHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        if (DataContext is DownloadRowViewModel vm && vm.OpenDownloadCommand.CanExecute(null))
            vm.OpenDownloadCommand.Execute(null);
    }

    /// <summary>Right-click selection semantics (issue #19): a right-click on a row that isn't
    /// already part of the selection makes it the sole selection first, so the context menu's
    /// "Remove" then acts on just that row; right-clicking within a multi-selection leaves it
    /// intact and "Remove" acts on the whole set.</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not DownloadRowViewModel vm)
            return;

        var selected = this.FindAncestorOfType<ListBox>()?.SelectedItems;
        if (selected is null || selected.Contains(vm))
            return;

        selected.Clear();
        selected.Add(vm);
    }

    /// <summary>Context-menu "Copy download link" - clipboard access needs a TopLevel, so it's
    /// handled here rather than in the view model.</summary>
    private async void OnCopyLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DownloadRowViewModel vm)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            await clipboard.SetTextAsync(vm.SourceUrl);
        }
        catch
        {
            // A clipboard that won't accept text (locked by another app, headless) isn't worth
            // interrupting the user over.
        }
    }
}
