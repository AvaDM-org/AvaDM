using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using AvaDM.UI.ViewModels;

namespace AvaDM.UI.Views;

public partial class AddDownloadView : UserControl
{
    public AddDownloadView()
    {
        InitializeComponent();
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddDownloadViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a destination folder",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is { } localPath)
            viewModel.DestinationPath = localPath;
    }
}
