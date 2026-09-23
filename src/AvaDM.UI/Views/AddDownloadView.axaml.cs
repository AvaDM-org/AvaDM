using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
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
            viewModel.SaveDirectory = localPath;
    }

    private async void OnImportClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddDownloadViewModel viewModel)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        string? text = null;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch
        {
            // Clipboard unavailable - treated the same as empty below.
        }

        viewModel.ImportLinks(text, "clipboard");
    }

    private async void OnImportFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddDownloadViewModel viewModel)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a text file with one link per line",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Text files") { Patterns = ["*.txt", "*.list", "*.lst"] },
                FilePickerFileTypes.All,
            ],
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            viewModel.ImportLinks(await reader.ReadToEndAsync(), "file");
        }
        catch (Exception ex)
        {
            viewModel.ErrorMessage = $"Could not read the file: {ex.Message}";
        }
    }
}
