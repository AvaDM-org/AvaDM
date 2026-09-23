using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AvaDM.UI.ViewModels;

namespace AvaDM.UI.Views;

public partial class AddDownloadView : UserControl
{
    private IPointer? _lastPointer;

    public AddDownloadView()
    {
        InitializeComponent();

        // The Click event doesn't expose the pointer, so remember it from the press.
        AddHandler(PointerPressedEvent, (_, e) => _lastPointer = e.Pointer, RoutingStrategies.Tunnel);
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

        // The system file picker swallows the mouse release, so the button that opened it would keep
        // pointer capture. A successful import replaces this whole dialog, leaving that capture on a
        // control that's no longer shown and dead-locking every click and scroll in the main window.
        _lastPointer?.Capture(null);

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
