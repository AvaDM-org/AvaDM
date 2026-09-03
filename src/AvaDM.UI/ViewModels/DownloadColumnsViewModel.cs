using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using AvaDM.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AvaDM.UI.ViewModels;

/// <summary>
/// Owns the downloads table's column layout for the redesign (issue #19): which columns exist,
/// their display order, which are visible, their (non-persisted) widths, and the active sort
/// column + direction. The header bar and every download row bind to <see cref="Columns"/> /
/// <see cref="VisibleTrailingColumns"/> so a reorder or show/hide reflows both at once.
///
/// Column order, visibility, and sort are persisted as one JSON blob under
/// <see cref="UiPreferencesRepository.DownloadListLayoutKey"/>; widths are intentionally left
/// out and reset to <see cref="DownloadColumnViewModel.DefaultWidth"/> each launch.
///
/// <see cref="DownloadColumnId.Name"/> is pinned: always visible, always index 0, never moved.
/// </summary>
public sealed partial class DownloadColumnsViewModel : ViewModelBase
{
    // Id, header text, can-hide, default width (px). Name's width is driven by the layout's
    // flexible "*" column, not this value, so it's left at 0.
    private static readonly IReadOnlyList<(DownloadColumnId Id, string Header, bool CanHide, double Width)> Meta =
    [
        (DownloadColumnId.Name, "Name", false, 0),
        (DownloadColumnId.Type, "Type", true, 70),
        (DownloadColumnId.Size, "Size", true, 90),
        (DownloadColumnId.Created, "Created", true, 130),
        (DownloadColumnId.Speed, "Speed", true, 150),
        (DownloadColumnId.ProgressPercent, "Progress %", true, 90),
        (DownloadColumnId.ProgressSize, "Progress size", true, 150),
        (DownloadColumnId.Status, "Status", true, 110),
    ];

    private static readonly DownloadColumnId[] DefaultOrder =
    [
        DownloadColumnId.Name,
        DownloadColumnId.Size,
        DownloadColumnId.ProgressPercent,
        DownloadColumnId.Speed,
        DownloadColumnId.Created,
        DownloadColumnId.Type,
        DownloadColumnId.ProgressSize,
        DownloadColumnId.Status,
    ];

    private static readonly HashSet<DownloadColumnId> DefaultVisible =
    [
        DownloadColumnId.Name,
        DownloadColumnId.Size,
        DownloadColumnId.ProgressPercent,
        DownloadColumnId.Speed,
        DownloadColumnId.Created,
    ];

    private const DownloadColumnId DefaultSortColumn = DownloadColumnId.Created;
    private const bool DefaultSortAscending = false;

    private readonly UiPreferencesRepository _preferences;

    /// <summary>True while <see cref="Load"/> is populating <see cref="Columns"/> - suppresses the
    /// persist/reflow side effects that the collection- and property-change handlers would
    /// otherwise fire for every row of the initial load.</summary>
    private bool _loading;

    /// <summary>All columns in display order (Name first). Never reassigned - mutated in place so
    /// bound headers/rows track it.</summary>
    public ObservableCollection<DownloadColumnViewModel> Columns { get; } = new();

    /// <summary>Visible columns except Name, in display order. The header's trailing cells and
    /// each row's trailing cells bind here; Name is rendered separately as the flexible column.</summary>
    public ObservableCollection<DownloadColumnViewModel> VisibleTrailingColumns { get; } = new();

    /// <summary>The pinned Name column (always <see cref="Columns"/>[0]). Its instance is stable
    /// for this view model's lifetime, so bound headers/rows can hold onto it directly.</summary>
    public DownloadColumnViewModel NameColumn { get; private set; } = null!;

    [ObservableProperty]
    private DownloadColumnId _sortColumnId = DefaultSortColumn;

    [ObservableProperty]
    private bool _sortAscending = DefaultSortAscending;

    /// <summary>Raised when the column set, order, or visibility changes - i.e. when bound
    /// headers/rows need to rebuild their cells (not on width changes, which bindings handle).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Raised when the sort column or direction changes, so the list re-sorts.</summary>
    public event EventHandler? SortChanged;

    /// <summary>The most recent fire-and-forget persistence write. Exposed for tests to await;
    /// production code never needs to.</summary>
    internal Task? LastPersistTask { get; private set; }

    public DownloadColumnsViewModel(UiPreferencesRepository preferences)
    {
        _preferences = preferences;
        Load();

        Columns.CollectionChanged += OnColumnsCollectionChanged;
    }

    private void Load()
    {
        _loading = true;
        try
        {
            string? json = null;
            try
            {
                json = _preferences.GetValueAsync(UiPreferencesRepository.DownloadListLayoutKey)
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // A preferences store that can't be read shouldn't block the downloads page -
                // fall back to the default layout, exactly like App.LoadStoredPreferences does.
            }

            var (order, visible, sort, asc) = ParseLayout(json);

            foreach (var col in Columns)
                col.PropertyChanged -= OnColumnPropertyChanged;
            Columns.Clear();

            foreach (var id in order)
            {
                var meta = Meta.First(m => m.Id == id);
                var col = new DownloadColumnViewModel(id, meta.Header, meta.CanHide, meta.Width, Sort, MoveColumnBy)
                {
                    IsVisible = id == DownloadColumnId.Name || visible.Contains(id),
                    Owner = this,
                };
                col.PropertyChanged += OnColumnPropertyChanged;
                Columns.Add(col);
            }

            NameColumn = Columns[0];
            SortColumnId = sort;
            SortAscending = asc;

            RebuildVisibleTrailing();
            RefreshSortGlyphs();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_loading)
            return;

        RebuildVisibleTrailing();
        Persist();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading)
            return;

        if (e.PropertyName == nameof(DownloadColumnViewModel.IsVisible))
        {
            RebuildVisibleTrailing();
            Persist();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RebuildVisibleTrailing()
    {
        VisibleTrailingColumns.Clear();
        foreach (var col in Columns)
        {
            if (col.Id != DownloadColumnId.Name && col.IsVisible)
                VisibleTrailingColumns.Add(col);
        }
    }

    private void RefreshSortGlyphs()
    {
        foreach (var col in Columns)
        {
            col.SortState = col.Id != SortColumnId
                ? ColumnSortState.None
                : SortAscending ? ColumnSortState.Ascending : ColumnSortState.Descending;
        }
    }

    partial void OnSortColumnIdChanged(DownloadColumnId value)
    {
        _ = value;
        if (_loading)
            return;

        RefreshSortGlyphs();
        Persist();
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSortAscendingChanged(bool value)
    {
        _ = value;
        if (_loading)
            return;

        RefreshSortGlyphs();
        Persist();
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Toggles a column's visibility (no-op for Name). Kept for tests / programmatic
    /// callers; the header menu binds <see cref="DownloadColumnViewModel.ToggleVisibilityCommand"/>
    /// directly.</summary>
    public void ToggleColumn(DownloadColumnViewModel? column)
    {
        if (column is null || !column.CanHide)
            return;

        column.IsVisible = !column.IsVisible;
    }

    /// <summary>Moves a column one slot earlier in display order, relative to the other reorderable
    /// columns (skips over Name and ignores hidden columns between it and its neighbour).</summary>
    public void MoveColumnLeft(DownloadColumnViewModel? column) => MoveColumnBy(column, -1);

    /// <summary>Moves a column one slot later in display order.</summary>
    public void MoveColumnRight(DownloadColumnViewModel? column) => MoveColumnBy(column, +1);

    /// <summary>Direction is -1 (left/earlier) or +1 (right/later). Reorders among the *visible*
    /// reorderable columns so a "move left" past a hidden column still has a visible effect.</summary>
    public void MoveColumnBy(DownloadColumnViewModel? column, int direction)
    {
        if (column is null || !column.CanReorder || direction == 0)
            return;

        var reorderable = Columns.Where(c => c.CanReorder && c.IsVisible).ToList();
        var index = reorderable.IndexOf(column);
        if (index < 0)
            return;

        var target = index + Math.Sign(direction);
        if (target < 0 || target >= reorderable.Count)
            return;

        var from = Columns.IndexOf(column);
        var to = Columns.IndexOf(reorderable[target]);
        if (from != to)
            Columns.Move(from, to);
    }

    /// <summary>Moves <paramref name="column"/> to where <paramref name="target"/> currently sits
    /// (Name is never displaced). Used by header drag-reorder - the view resolves which column
    /// the pointer was dropped over.</summary>
    public void MoveColumnBefore(DownloadColumnViewModel? column, DownloadColumnViewModel? target)
    {
        if (column is null || target is null || column == target || !column.CanReorder)
            return;

        var from = Columns.IndexOf(column);
        var to = Columns.IndexOf(target);
        if (from < 0 || to < 0)
            return;

        to = Math.Max(1, to);
        if (from != to)
            Columns.Move(from, to);
    }

    /// <summary>Header click: sort by this column, flipping direction if it's already the sort
    /// column. Switching to a new column starts ascending for text columns, descending otherwise
    /// (newest/biggest/most-complete first is the more useful default there).</summary>
    public void Sort(DownloadColumnViewModel? column)
    {
        if (column is null)
            return;

        if (SortColumnId == column.Id)
        {
            SortAscending = !SortAscending;
            return;
        }

        SortColumnId = column.Id;
        SortAscending = column.Id is DownloadColumnId.Name or DownloadColumnId.Type;
    }

    private void Persist()
    {
        if (_loading)
            return;

        var json = SerializeLayout(Columns, SortColumnId, SortAscending);
        LastPersistTask = _preferences.SetValueAsync(UiPreferencesRepository.DownloadListLayoutKey, json);
    }

    // ----- pure serialization helpers (unit-tested directly, no database) -----

    private sealed record LayoutDto(List<string> Order, List<string> Hidden, string Sort, bool Asc);

    internal static string SerializeLayout(
        IEnumerable<DownloadColumnViewModel> columns, DownloadColumnId sort, bool ascending)
    {
        var list = columns.ToList();
        var dto = new LayoutDto(
            list.Select(c => c.Id.ToString()).ToList(),
            list.Where(c => !c.IsVisible).Select(c => c.Id.ToString()).ToList(),
            sort.ToString(),
            ascending);
        return JsonSerializer.Serialize(dto);
    }

    /// <summary>Parses a stored layout blob into a normalized (order, visible-set, sort, asc)
    /// tuple. Always returns a usable layout: unknown/duplicate column names are dropped, columns
    /// missing from the blob (e.g. added in a newer build) are appended in default order, Name is
    /// forced visible at index 0, and any malformed input falls back to the full default layout.</summary>
    internal static (List<DownloadColumnId> Order, HashSet<DownloadColumnId> Visible, DownloadColumnId Sort, bool Ascending)
        ParseLayout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return DefaultLayout();

        LayoutDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<LayoutDto>(json);
        }
        catch (JsonException)
        {
            return DefaultLayout();
        }

        if (dto is null)
            return DefaultLayout();

        var order = new List<DownloadColumnId>();
        foreach (var name in dto.Order ?? [])
        {
            if (Enum.TryParse<DownloadColumnId>(name, ignoreCase: false, out var id)
                && Meta.Any(m => m.Id == id) && !order.Contains(id))
            {
                order.Add(id);
            }
        }

        foreach (var meta in Meta)
        {
            if (!order.Contains(meta.Id))
                order.Add(meta.Id);
        }

        order.Remove(DownloadColumnId.Name);
        order.Insert(0, DownloadColumnId.Name);

        var hidden = new HashSet<DownloadColumnId>();
        foreach (var name in dto.Hidden ?? [])
        {
            if (Enum.TryParse<DownloadColumnId>(name, ignoreCase: false, out var id))
                hidden.Add(id);
        }

        hidden.Remove(DownloadColumnId.Name);
        var visible = Meta.Select(m => m.Id).Where(id => !hidden.Contains(id)).ToHashSet();

        var sort = Enum.TryParse<DownloadColumnId>(dto.Sort, ignoreCase: false, out var sortId)
            && Meta.Any(m => m.Id == sortId)
            ? sortId
            : DefaultSortColumn;

        return (order, visible, sort, dto.Asc);
    }

    private static (List<DownloadColumnId>, HashSet<DownloadColumnId>, DownloadColumnId, bool) DefaultLayout() =>
        (DefaultOrder.ToList(), [.. DefaultVisible], DefaultSortColumn, DefaultSortAscending);
}
