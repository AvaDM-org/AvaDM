using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaDM.UI.ViewModels;

namespace AvaDM.UI.Views;

public partial class DownloadRowView : UserControl
{
    public DownloadRowView()
    {
        InitializeComponent();
    }

    /// <summary>Opens the completed download on a double-click anywhere in the collapsed header
    /// (the name, destination, progress, status, etc.) - except on one of the header's own icon
    /// buttons, which already have their own click behavior and shouldn't also trigger this.</summary>
    private void OnHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        if (DataContext is DownloadRowViewModel vm && vm.OpenDownloadCommand.CanExecute(null))
            vm.OpenDownloadCommand.Execute(null);
    }
}
