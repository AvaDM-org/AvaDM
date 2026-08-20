using Avalonia;
using Avalonia.Controls;

namespace AvaDM.UI.Controls;

/// <summary>
/// Reusable status chip (docs/design.md components.status-chip). <see cref="StatusClass"/> takes
/// the plain semantic string a row/chunk view model already computes (e.g.
/// <c>DownloadRowViewModel.StatusChipClass</c>) - "success", "info", "warning", "danger", or
/// "neutral", matching the <c>Border.chip.*</c> style classes defined in
/// <c>Styles/StatusChip.axaml</c>. It's applied to <see cref="ChipBorder"/>'s Classes from
/// code-behind instead of an AXAML binding, because Avalonia's Classes collection has no direct
/// string-to-classes binding support.
/// </summary>
public partial class StatusChip : UserControl
{
    private static readonly string[] SemanticClasses = ["success", "info", "warning", "danger", "neutral"];

    public static readonly StyledProperty<string> StatusClassProperty =
        AvaloniaProperty.Register<StatusChip, string>(nameof(StatusClass), "neutral");

    public static readonly StyledProperty<string> StatusTextProperty =
        AvaloniaProperty.Register<StatusChip, string>(nameof(StatusText), string.Empty);

    public StatusChip()
    {
        InitializeComponent();

        // The default value above doesn't raise a property-changed notification, so the initial
        // class has to be applied explicitly here.
        ApplyStatusClass(StatusClass);
    }

    public string StatusClass
    {
        get => GetValue(StatusClassProperty);
        set => SetValue(StatusClassProperty, value);
    }

    public string StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StatusClassProperty)
            ApplyStatusClass(change.GetNewValue<string>());
    }

    private void ApplyStatusClass(string statusClass)
    {
        foreach (var known in SemanticClasses)
            ChipBorder.Classes.Remove(known);

        if (!string.IsNullOrEmpty(statusClass))
            ChipBorder.Classes.Add(statusClass);
    }
}
