using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using AvaDM.UI.ViewModels;

namespace AvaDM.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnBrowseDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the default download folder",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is { } localPath)
            viewModel.DownloadDirectory = localPath;
    }
}
