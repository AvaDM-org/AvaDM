using AvaDM.Core;
using AvaDM.UI.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>Overlay-hosted Add Download form; conflict handling mirrors the console's
/// --resume/--overwrite/--rename flags (see <c>src/AvaDM.Console/Program.cs</c>'s
/// <c>Start</c> local function), but is inline instead of a CLI prompt.</summary>
public sealed partial class AddDownloadViewModel : ViewModelBase
{
    private readonly DownloadManager _downloadManager;
    private readonly Action<DownloadRecord, DownloadHandle> _onSubmitted;
    private readonly Action _onCancelled;

    private Uri? _pendingUri;
    private string? _pendingDestination;
    private DownloadOptions? _pendingOptions;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _destinationPath = string.Empty;

    [ObservableProperty]
    private bool _isAdvancedExpanded;

    [ObservableProperty]
    private string? _chunkCountInput;

    [ObservableProperty]
    private string? _speedLimitInput;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveOverwriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveRenameCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResolveResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveOverwriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveRenameCommand))]
    private bool _hasConflict;

    [ObservableProperty]
    private string? _conflictMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public AddDownloadViewModel(
        DownloadManager downloadManager,
        Action<DownloadRecord, DownloadHandle> onSubmitted,
        Action onCancelled)
    {
        _downloadManager = downloadManager;
        _onSubmitted = onSubmitted;
        _onCancelled = onCancelled;
    }

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        ErrorMessage = null;
        HasConflict = false;
        ConflictMessage = null;

        var trimmedUrl = Url.Trim();
        if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri))
        {
            ErrorMessage = "Enter a valid absolute URL (e.g. https://example.com/file.zip).";
            IsBusy = false;
            return;
        }

        int? chunkCount;
        if (string.IsNullOrWhiteSpace(ChunkCountInput))
        {
            chunkCount = null;
        }
        else if (int.TryParse(ChunkCountInput.Trim(), out var cc) && cc > 0)
        {
            chunkCount = cc;
        }
        else
        {
            ErrorMessage = "Chunk count must be a positive whole number.";
            IsBusy = false;
            return;
        }

        long? speedLimit;
        var text = SpeedLimitInput?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            speedLimit = null;
        }
        else if (long.TryParse(text, out var bps) && bps > 0)
        {
            speedLimit = bps;
        }
        else
        {
            ErrorMessage = "Speed limit must be a positive number of bytes/sec, or \"off\".";
            IsBusy = false;
            return;
        }

        var destination = string.IsNullOrWhiteSpace(DestinationPath) ? null : DestinationPath.Trim();

        IsBusy = true;
        var conflict = await _downloadManager.CheckConflictAsync(uri, destination);

        _pendingUri = uri;
        _pendingDestination = destination;
        _pendingOptions = new DownloadOptions
        {
            ChunkCount = chunkCount,
            InitialSpeedLimitBytesPerSecond = speedLimit,
        };

        if (conflict.HasConflict && conflict.ExistingRecord is not null)
        {
            ConflictMessage = $"A download to \"{conflict.ExistingRecord.DestinationPath}\" already exists ({conflict.ExistingRecord.State}, {FormatHelpers.FormatBytes(conflict.ExistingRecord.BytesDownloaded)} of {(conflict.ExistingRecord.TotalBytes > 0 ? FormatHelpers.FormatBytes(conflict.ExistingRecord.TotalBytes) : "unknown size")} downloaded).";
            HasConflict = true;
            IsBusy = false;
            return;
        }

        await CompleteAddAsync(resolution: null);
    }

    [RelayCommand(CanExecute = nameof(CanResolveConflict))]
    private async Task ResolveResume() => await CompleteAddAsync(new ConflictResolution.Resume());

    [RelayCommand(CanExecute = nameof(CanResolveConflict))]
    private async Task ResolveOverwrite() => await CompleteAddAsync(new ConflictResolution.Overwrite());

    [RelayCommand(CanExecute = nameof(CanResolveConflict))]
    private async Task ResolveRename() => await CompleteAddAsync(
        new ConflictResolution.RenameDestination(
            string.IsNullOrWhiteSpace(DestinationPath) ? _pendingDestination ?? string.Empty : DestinationPath.Trim()));

    [RelayCommand]
    private void Cancel() => _onCancelled();

    private bool CanSubmit() => !IsBusy;

    private bool CanResolveConflict() => HasConflict && !IsBusy;

    private async Task CompleteAddAsync(ConflictResolution? resolution)
    {
        IsBusy = true;
        HasConflict = false;
        var result = await _downloadManager.AddDownloadAsync(_pendingUri!, _pendingDestination, _pendingOptions, resolution);
        IsBusy = false;

        if (!result.Success)
        {
            if (result.Conflict?.HasConflict == true)
            {
                // Rename landed on another existing destination - surface it as a fresh conflict rather than a plain error.
                HasConflict = true;
                var existing = result.Conflict.ExistingRecord;
                ConflictMessage = existing is null
                    ? "That destination is also already in use."
                    : $"\"{existing.DestinationPath}\" is also already in use ({existing.State}).";
            }
            else
            {
                ErrorMessage = result.Error ?? "Could not start the download.";
            }
            return;
        }

        var record = await _downloadManager.GetDownloadAsync(result.Id!.Value);
        if (record is null || result.Handle is null)
        {
            ErrorMessage = "Download started but its record could not be loaded.";
            return;
        }

        _onSubmitted(record, result.Handle);
    }
}
