using AvaDM.UI.Services;
using AvaDM.UI.ViewModels;
using Xunit;

namespace AvaDM.UI.Tests;

public sealed class DownloadColumnsViewModelTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("avadm-ui-tests-").FullName;

    private UiPreferencesRepository NewPreferences()
    {
        var repo = new UiPreferencesRepository(Path.Combine(_tempDirectory, "avadm.db"));
        repo.InitializeAsync().GetAwaiter().GetResult();
        return repo;
    }

    private static IReadOnlyList<DownloadColumnId> TrailingIds(DownloadColumnsViewModel vm) =>
        vm.VisibleTrailingColumns.Select(c => c.Id).ToList();

    [Fact]
    public void FreshInstall_UsesTheDefaultLayout()
    {
        var vm = new DownloadColumnsViewModel(NewPreferences());

        Assert.Equal(DownloadColumnId.Name, vm.Columns[0].Id);
        Assert.All(vm.Columns, c => Assert.True(c.Id != DownloadColumnId.Name || (c.IsVisible && !c.CanHide && !c.CanReorder)));
        Assert.Equal(
            new[] { DownloadColumnId.Size, DownloadColumnId.ProgressPercent, DownloadColumnId.Speed, DownloadColumnId.Created },
            TrailingIds(vm));
        Assert.Equal(DownloadColumnId.Created, vm.SortColumnId);
        Assert.False(vm.SortAscending);
    }

    [Fact]
    public void ToggleColumn_HidesThenShowsATrailingColumn()
    {
        var vm = new DownloadColumnsViewModel(NewPreferences());
        var size = vm.Columns.First(c => c.Id == DownloadColumnId.Size);

        vm.ToggleColumn(size);
        Assert.DoesNotContain(DownloadColumnId.Size, TrailingIds(vm));

        vm.ToggleColumn(size);
        Assert.Contains(DownloadColumnId.Size, TrailingIds(vm));
    }

    [Fact]
    public void ToggleColumn_IgnoresTheNameColumn()
    {
        var vm = new DownloadColumnsViewModel(NewPreferences());
        var name = vm.Columns.First(c => c.Id == DownloadColumnId.Name);

        vm.ToggleColumn(name);

        Assert.True(name.IsVisible);
    }

    [Fact]
    public void MoveColumnRight_ReordersAmongVisibleColumnsAndClampsAtTheEnd()
    {
        var vm = new DownloadColumnsViewModel(NewPreferences());
        var size = vm.Columns.First(c => c.Id == DownloadColumnId.Size);

        vm.MoveColumnRight(size);
        Assert.Equal(
            new[] { DownloadColumnId.ProgressPercent, DownloadColumnId.Size, DownloadColumnId.Speed, DownloadColumnId.Created },
            TrailingIds(vm));

        var created = vm.Columns.First(c => c.Id == DownloadColumnId.Created);
        vm.MoveColumnRight(created); // already last visible - no-op
        Assert.Equal(DownloadColumnId.Created, TrailingIds(vm)[^1]);
    }

    [Fact]
    public void MoveColumnLeft_NeverMovesAColumnAheadOfName()
    {
        var vm = new DownloadColumnsViewModel(NewPreferences());
        var size = vm.Columns.First(c => c.Id == DownloadColumnId.Size);

        vm.MoveColumnLeft(size); // Size is already the first trailing column

        Assert.Equal(DownloadColumnId.Name, vm.Columns[0].Id);
        Assert.Equal(DownloadColumnId.Size, vm.Columns[1].Id);
    }

    [Fact]
    public void Sort_FlipsDirectionForTheSameColumnAndResetsWhenSwitching()
    {
        var vm = new DownloadColumnsViewModel(NewPreferences());
        var created = vm.Columns.First(c => c.Id == DownloadColumnId.Created);
        var name = vm.Columns.First(c => c.Id == DownloadColumnId.Name);

        vm.Sort(created); // same as default sort column -> flip to ascending
        Assert.True(vm.SortAscending);
        Assert.Equal(ColumnSortState.Ascending, created.SortState);

        vm.Sort(name); // switch to a text column -> ascending by default
        Assert.Equal(DownloadColumnId.Name, vm.SortColumnId);
        Assert.True(vm.SortAscending);
        Assert.Equal(ColumnSortState.None, created.SortState);

        var size = vm.Columns.First(c => c.Id == DownloadColumnId.Size);
        vm.Sort(size); // switch to a numeric column -> descending by default
        Assert.False(vm.SortAscending);
    }

    [Fact]
    public async Task Layout_RoundTripsThroughThePreferencesStore()
    {
        var preferences = NewPreferences();

        var first = new DownloadColumnsViewModel(preferences);
        first.ToggleColumn(first.Columns.First(c => c.Id == DownloadColumnId.Size));       // hide Size
        first.ToggleColumn(first.Columns.First(c => c.Id == DownloadColumnId.Type));       // show Type
        first.MoveColumnRight(first.Columns.First(c => c.Id == DownloadColumnId.Speed));
        first.Sort(first.Columns.First(c => c.Id == DownloadColumnId.ProgressPercent));
        await first.LastPersistTask!;

        var second = new DownloadColumnsViewModel(preferences);

        Assert.Equal(
            first.Columns.Select(c => c.Id),
            second.Columns.Select(c => c.Id));
        Assert.Equal(TrailingIds(first), TrailingIds(second));
        Assert.Equal(first.SortColumnId, second.SortColumnId);
        Assert.Equal(first.SortAscending, second.SortAscending);
        Assert.DoesNotContain(DownloadColumnId.Size, TrailingIds(second));
        Assert.Contains(DownloadColumnId.Type, TrailingIds(second));
    }

    [Fact]
    public void ParseLayout_MalformedInput_FallsBackToDefaults()
    {
        var (order, visible, sort, ascending) = DownloadColumnsViewModel.ParseLayout("{ not json");

        Assert.Equal(DownloadColumnId.Name, order[0]);
        Assert.Equal(8, order.Count);
        Assert.Contains(DownloadColumnId.Size, visible);
        Assert.DoesNotContain(DownloadColumnId.Type, visible);
        Assert.Equal(DownloadColumnId.Created, sort);
        Assert.False(ascending);
    }

    [Fact]
    public void ParseLayout_DropsUnknownNamesForcesNameFirstAndAppendsMissingColumns()
    {
        var json = DownloadColumnsViewModel.SerializeLayout(
            BuildColumns(DownloadColumnId.Speed, DownloadColumnId.Name),
            DownloadColumnId.Speed,
            ascending: true);
        // inject a bogus entry
        json = json.Replace("[\"Speed\",\"Name\"]", "[\"Speed\",\"Bogus\",\"Name\"]");

        var (order, _, sort, ascending) = DownloadColumnsViewModel.ParseLayout(json);

        Assert.Equal(DownloadColumnId.Name, order[0]);
        Assert.Equal(DownloadColumnId.Speed, order[1]);
        Assert.Equal(8, order.Count);
        Assert.Equal(order.Distinct().Count(), order.Count);
        Assert.Equal(DownloadColumnId.Speed, sort);
        Assert.True(ascending);
    }

    private static IEnumerable<DownloadColumnViewModel> BuildColumns(params DownloadColumnId[] ids) =>
        ids.Select(id => new DownloadColumnViewModel(id, id.ToString(), id != DownloadColumnId.Name, 100)
        {
            IsVisible = true,
        });

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
