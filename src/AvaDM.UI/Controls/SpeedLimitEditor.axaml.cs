using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaDM.UI.Controls;

/// <summary>
/// Bindable speed-limit editor (docs/design.md speed-limit controls). Wraps a NumericUpDown in
/// MB/s - so increment/decrement is a spinner click rather than typing a raw byte count - and an
/// Unlimited checkbox, converting to/from the <see cref="long"/> bytes/sec the download engine
/// (<see cref="AvaDM.Core.DownloadHandle.SetSpeedLimit"/>) actually takes. Bindings live on
/// <see cref="SpeedLimitBytesPerSecondProperty"/> alone; the MB value and Unlimited state are
/// private editor state, not separately bindable, since nothing outside this control needs them.
/// </summary>
public partial class SpeedLimitEditor : UserControl
{
    private const decimal BytesPerMegabyte = 1024 * 1024;
    private const decimal DefaultMegabytes = 1m;

    public static readonly StyledProperty<long?> SpeedLimitBytesPerSecondProperty =
        AvaloniaProperty.Register<SpeedLimitEditor, long?>(
            nameof(SpeedLimitBytesPerSecond),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private bool _syncing;

    public SpeedLimitEditor()
    {
        InitializeComponent();
        SyncControlsFromBytes(SpeedLimitBytesPerSecond);

        MegabytesUpDown.ValueChanged += OnMegabytesChanged;
        UnlimitedCheckBox.IsCheckedChanged += OnUnlimitedChanged;
    }

    public long? SpeedLimitBytesPerSecond
    {
        get => GetValue(SpeedLimitBytesPerSecondProperty);
        set => SetValue(SpeedLimitBytesPerSecondProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SpeedLimitBytesPerSecondProperty && !_syncing)
            SyncControlsFromBytes(change.GetNewValue<long?>());
    }

    /// <summary>Pushes an externally-set (or initial) bytes/sec value into the two child
    /// controls. Guarded by <see cref="_syncing"/> against the child event handlers below
    /// bouncing the value straight back through <see cref="SpeedLimitBytesPerSecondProperty"/>.</summary>
    private void SyncControlsFromBytes(long? bytesPerSecond)
    {
        _syncing = true;
        try
        {
            UnlimitedCheckBox.IsChecked = bytesPerSecond is null;
            MegabytesUpDown.IsEnabled = bytesPerSecond is not null;
            MegabytesUpDown.Value = bytesPerSecond is { } bps
                ? bps / BytesPerMegabyte
                : MegabytesUpDown.Value ?? DefaultMegabytes;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnMegabytesChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_syncing || UnlimitedCheckBox.IsChecked == true)
            return;

        ApplyFromControls();
    }

    private void OnUnlimitedChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;

        MegabytesUpDown.IsEnabled = UnlimitedCheckBox.IsChecked != true;
        ApplyFromControls();
    }

    private void ApplyFromControls()
    {
        _syncing = true;
        try
        {
            SpeedLimitBytesPerSecond = UnlimitedCheckBox.IsChecked == true
                ? null
                : (long)Math.Max(1, Math.Round((MegabytesUpDown.Value ?? DefaultMegabytes) * BytesPerMegabyte));
        }
        finally
        {
            _syncing = false;
        }
    }
}
