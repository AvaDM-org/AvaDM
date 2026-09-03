using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AvaDM.UI.ViewModels;

namespace AvaDM.UI.Views;

public partial class DownloadListView : UserControl
{
    private const double MinColumnWidth = 48;
    private const double DragThreshold = 6;

    private DownloadColumnViewModel? _dragColumn;
    private double _dragStartX;
    private bool _dragging;

    public DownloadListView()
    {
        InitializeComponent();
    }

    /// <summary>Clipboard-paste icon inside the quick-add box: drop the clipboard text into it.
    /// Clipboard access needs a TopLevel, hence the code-behind.</summary>
    private async void OnPasteQuickAdd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DownloadListViewModel vm)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
                vm.QuickAddText = text.Trim();
        }
        catch
        {
            // Nothing useful on the clipboard, or it's unavailable - not worth surfacing.
        }
    }

    /// <summary>Column-resize grip drag: widen/narrow the column under the grip.</summary>
    private void OnHeaderGripDragDelta(object? sender, VectorEventArgs e)
    {
        if (sender is Control { DataContext: DownloadColumnViewModel column })
            column.Width = System.Math.Max(MinColumnWidth, column.Width + e.Vector.X);
    }

    // --- header drag-to-reorder (issue #19) -------------------------------------------------
    // A press that turns into a horizontal drag reorders the column; a press that stays put
    // falls through to the header Button's click-to-sort.

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DownloadColumnViewModel column }
            && column.CanReorder
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragColumn = column;
            _dragStartX = e.GetPosition(this).X;
            _dragging = false;
        }
    }

    private void OnHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragColumn is not null && !_dragging
            && System.Math.Abs(e.GetPosition(this).X - _dragStartX) > DragThreshold)
        {
            _dragging = true;
        }
    }

    private void OnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragColumn is not null && _dragging && DataContext is DownloadListViewModel vm)
        {
            var target = TrailingHeaderColumnAt(e.GetPosition(TrailingHeaders).X, vm);
            if (target is not null)
                vm.Columns.MoveColumnBefore(_dragColumn, target);
            e.Handled = true;
        }

        _dragColumn = null;
        _dragging = false;
    }

    /// <summary>Which visible trailing column the given x offset (within the trailing-header
    /// strip) falls over, by accumulating column widths.</summary>
    private static DownloadColumnViewModel? TrailingHeaderColumnAt(double x, DownloadListViewModel vm)
    {
        var acc = 0.0;
        foreach (var column in vm.Columns.VisibleTrailingColumns)
        {
            acc += column.Width;
            if (x < acc)
                return column;
        }

        return vm.Columns.VisibleTrailingColumns.Count > 0
            ? vm.Columns.VisibleTrailingColumns[^1]
            : null;
    }
}
