using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using AvaDM.UI.ViewModels;

namespace AvaDM.UI.Controls;

/// <summary>
/// The download row's aggregate progress bar (issue #19). Instead of one fill, it lays out one
/// flat segment per connection, each sized to that connection's share of the file and filled by
/// that connection's own progress - so the single bar visibly fills unevenly as connections run
/// at different rates. Falls back to a plain (optionally indeterminate) bar when there are no
/// per-connection snapshots yet or the total size is unknown.
/// </summary>
public partial class SegmentedProgressBar : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ChunksProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, IEnumerable?>(nameof(Chunks));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, bool>(nameof(IsIndeterminate));

    private INotifyCollectionChanged? _observed;

    public SegmentedProgressBar()
    {
        InitializeComponent();
        Rebuild();
    }

    public IEnumerable? Chunks
    {
        get => GetValue(ChunksProperty);
        set => SetValue(ChunksProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChunksProperty)
        {
            if (_observed is not null)
                _observed.CollectionChanged -= OnChunksCollectionChanged;

            _observed = change.GetNewValue<IEnumerable?>() as INotifyCollectionChanged;
            if (_observed is not null)
                _observed.CollectionChanged += OnChunksCollectionChanged;

            Rebuild();
        }
        else if (change.Property == IsIndeterminateProperty || change.Property == ValueProperty)
        {
            Rebuild();
        }
    }

    private void OnChunksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Segments.Children.Clear();
        Segments.ColumnDefinitions.Clear();

        if (IsIndeterminate)
        {
            Segments.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Segments.Children.Add(NewSegment(indeterminate: true));
            return;
        }

        var chunks = Chunks?.OfType<ChunkRowViewModel>().ToList() ?? [];
        if (chunks.Count == 0)
        {
            Segments.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var bar = NewSegment(indeterminate: false);
            bar[!RangeBase.ValueProperty] = new Binding(nameof(Value)) { Source = this };
            Segments.Children.Add(bar);
            return;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var weight = Math.Max(chunk.TotalBytes, 1);
            Segments.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(weight, GridUnitType.Star)));

            var bar = NewSegment(indeterminate: false);
            bar[!RangeBase.ValueProperty] = new Binding(nameof(ChunkRowViewModel.ProgressPercent)) { Source = chunk };
            Grid.SetColumn(bar, i);
            Segments.Children.Add(bar);
        }
    }

    private static ProgressBar NewSegment(bool indeterminate) => new()
    {
        Classes = { "segment" },
        Minimum = 0,
        Maximum = 100,
        IsIndeterminate = indeterminate,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };
}
