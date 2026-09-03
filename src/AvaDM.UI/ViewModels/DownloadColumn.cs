using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>
/// One selectable column in the downloads table. <see cref="Name"/> is special everywhere: it is
/// always visible, always the leftmost column, and can never be reordered - the redesign
/// (issue #19) pins it so a download is always identifiable no matter how the other columns are
/// arranged. Persisted by name (not ordinal) via <see cref="DownloadColumnsViewModel"/>, so the
/// enum can be extended without invalidating a saved layout.
/// </summary>
public enum DownloadColumnId
{
    Name,
    Type,
    Size,
    Created,
    Speed,
    ProgressPercent,
    ProgressSize,
    Status,
}

/// <summary>Which sort marker (if any) a column header shows.</summary>
public enum ColumnSortState
{
    None,
    Ascending,
    Descending,
}

/// <summary>
/// View model for a single column header: its identity, label, current visibility and pixel
/// width (both user-adjustable), the sort marker it currently displays, and the header-cell
/// commands (sort / move / toggle). The commands delegate straight back to the owning
/// <see cref="DownloadColumnsViewModel"/> so a header <c>DataTemplate</c> can bind them directly
/// without reaching across the visual tree. Widths are live state only -
/// <see cref="DownloadColumnsViewModel"/> never persists them.
/// </summary>
public sealed partial class DownloadColumnViewModel : ViewModelBase
{
    private readonly Action<DownloadColumnViewModel>? _sort;
    private readonly Action<DownloadColumnViewModel, int>? _move;

    /// <summary>The columns view model this belongs to - lets a header cell's context menu bind
    /// the full column list (<c>Owner.Columns</c>) for its show/hide submenu without reaching
    /// across the visual tree.</summary>
    public DownloadColumnsViewModel? Owner { get; internal set; }

    public DownloadColumnId Id { get; }

    public string Header { get; }

    /// <summary>False only for <see cref="DownloadColumnId.Name"/> - every other column can be
    /// toggled off from the header's right-click menu.</summary>
    public bool CanHide { get; }

    /// <summary>False only for <see cref="DownloadColumnId.Name"/> (pinned leftmost).</summary>
    public bool CanReorder => CanHide;

    public double DefaultWidth { get; }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortGlyph))]
    private ColumnSortState _sortState;

    public DownloadColumnViewModel(
        DownloadColumnId id,
        string header,
        bool canHide,
        double defaultWidth,
        Action<DownloadColumnViewModel>? sort = null,
        Action<DownloadColumnViewModel, int>? move = null)
    {
        Id = id;
        Header = header;
        CanHide = canHide;
        DefaultWidth = defaultWidth;
        _width = defaultWidth;
        _isVisible = true;
        _sort = sort;
        _move = move;
    }

    /// <summary>Arrow shown next to the header label: up for ascending, down for descending,
    /// nothing when this isn't the active sort column. A plain string rather than an AXAML
    /// converter, matching this project's converter-free view-model convention.</summary>
    public string SortGlyph => SortState switch
    {
        ColumnSortState.Ascending => "▴",
        ColumnSortState.Descending => "▾",
        _ => string.Empty,
    };

    /// <summary>Header-cell click: sort by this column (owner flips direction if it's already
    /// the active sort column).</summary>
    [RelayCommand]
    private void Sort() => _sort?.Invoke(this);

    /// <summary>Header context menu: move this column one slot earlier / later.</summary>
    [RelayCommand]
    private void MoveLeft() => _move?.Invoke(this, -1);

    [RelayCommand]
    private void MoveRight() => _move?.Invoke(this, +1);

    /// <summary>Header context menu: show/hide this column (no-op for <see cref="DownloadColumnId.Name"/>).
    /// The owner reacts to the <see cref="IsVisible"/> change to reflow and persist.</summary>
    [RelayCommand]
    private void ToggleVisibility()
    {
        if (CanHide)
            IsVisible = !IsVisible;
    }
}
