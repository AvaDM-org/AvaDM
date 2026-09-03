using CommunityToolkit.Mvvm.ComponentModel;

namespace AvaDM.UI.ViewModels;

/// <summary>
/// One trailing-column cell in a download row (issue #19). Pairs a shared
/// <see cref="DownloadColumnViewModel"/> (identity + width, one instance across every row) with
/// the <see cref="DownloadRowViewModel"/> whose value it shows. The row rebuilds its
/// <c>Cells</c> collection whenever columns are toggled or reordered, and calls
/// <see cref="Refresh"/> on each cell when its own progress / speed / state changes.
/// </summary>
public sealed partial class DownloadCellViewModel : ViewModelBase
{
    public DownloadColumnViewModel Column { get; }

    public DownloadRowViewModel Row { get; }

    public DownloadColumnId Id => Column.Id;

    /// <summary>The Status cell renders the shared status chip; every other cell renders
    /// <see cref="Text"/> plus an optional secondary <see cref="SubText"/> line.</summary>
    public bool IsStatus => Id == DownloadColumnId.Status;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubText))]
    private string _subText = string.Empty;

    public bool HasSubText => !string.IsNullOrEmpty(SubText);

    public DownloadCellViewModel(DownloadColumnViewModel column, DownloadRowViewModel row)
    {
        Column = column;
        Row = row;
        Refresh();
    }

    public void Refresh()
    {
        Text = Id switch
        {
            DownloadColumnId.Type => Row.Extension,
            DownloadColumnId.Size => Row.SizeText,
            DownloadColumnId.Created => Row.CreatedText,
            DownloadColumnId.Speed => Row.SpeedText,
            DownloadColumnId.ProgressPercent => Row.ProgressPercentText,
            DownloadColumnId.ProgressSize => Row.BytesText,
            _ => string.Empty,
        };

        SubText = Id is DownloadColumnId.ProgressPercent or DownloadColumnId.ProgressSize
            ? Row.RunningEtaText
            : string.Empty;
    }
}
