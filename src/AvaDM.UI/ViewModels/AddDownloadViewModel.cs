using System.IO;
using AvaDM.Core;
using AvaDM.UI.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>Overlay-hosted Add Download form; conflict handling mirrors the console's
/// --resume/--overwrite/--rename flags (see <c>src/AvaDM.Console/Program.cs</c>'s
/// <c>Start</c> local function), but is inline instead of a CLI prompt.
///
/// The destination is entered as a directory (<see cref="SaveDirectory"/>) plus a file name
/// (<see cref="FileName"/>) rather than a single path box: the file name is seeded from the URL
/// (<see cref="Downloader.SuggestFileName"/>, the same value the engine would pick) as soon as a
/// URL is typed, and stays editable so the on-disk name can differ from the URL's. The two are
/// combined into the single path the engine expects only at submit time.</summary>
public sealed partial class AddDownloadViewModel : ViewModelBase
{
    private readonly DownloadManager _downloadManager;
    private readonly DownloadSettings _settings;
    private readonly Action<DownloadRecord, DownloadHandle> _onSubmitted;
    private readonly Action _onCancelled;

    /// <summary>The file name last auto-derived from the URL. While <see cref="FileName"/> still
    /// equals this (or is empty), a URL edit is free to replace it; once the user types their own
    /// name it diverges and we stop overwriting their choice.</summary>
    private string _autoFilledName = string.Empty;

    private Uri? _pendingUri;
    private string? _pendingDestination;
    private DownloadOptions? _pendingOptions;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private bool _isAdvancedExpanded;

    [ObservableProperty]
    private string? _chunkCountInput;

    [ObservableProperty]
    private long? _speedLimitBytesPerSecond;

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

    /// <summary>Placeholder for the connections field, naming the actual default it falls back to
    /// (<see cref="DownloadSettings.DefaultChunkCount"/>, itself editable in Settings).</summary>
    public string ConnectionsPlaceholder => $"Default ({_settings.DefaultChunkCount})";

    public AddDownloadViewModel(
        DownloadManager downloadManager,
        DownloadSettings settings,
        Action<DownloadRecord, DownloadHandle> onSubmitted,
        Action onCancelled)
    {
        _downloadManager = downloadManager;
        _settings = settings;
        _onSubmitted = onSubmitted;
        _onCancelled = onCancelled;
    }

    partial void OnUrlChanged(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return;

        // Only seed the name while the user hasn't taken it over.
        if (!string.IsNullOrEmpty(FileName) && FileName != _autoFilledName)
            return;

        _autoFilledName = Downloader.SuggestFileName(uri);
        FileName = _autoFilledName;
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
            ErrorMessage = "Connections must be a positive whole number.";
            IsBusy = false;
            return;
        }

        var destination = BuildDestination(uri, out var destinationError);
        if (destinationError is not null)
        {
            ErrorMessage = destinationError;
            IsBusy = false;
            return;
        }

        IsBusy = true;
        var conflict = await _downloadManager.CheckConflictAsync(uri, destination);

        _pendingUri = uri;
        _pendingDestination = destination;
        _pendingOptions = new DownloadOptions
        {
            ChunkCount = chunkCount,
            InitialSpeedLimitBytesPerSecond = SpeedLimitBytesPerSecond,
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
    private async Task ResolveRename()
    {
        var destination = _pendingDestination ?? string.Empty;
        if (_pendingUri is not null)
        {
            var rebuilt = BuildDestination(_pendingUri, out var error);
            if (error is not null)
            {
                ErrorMessage = error;
                return;
            }
            destination = rebuilt;
        }

        await CompleteAddAsync(new ConflictResolution.RenameDestination(destination));
    }

    [RelayCommand]
    private void Cancel() => _onCancelled();

    private bool CanSubmit() => !IsBusy;

    private bool CanResolveConflict() => HasConflict && !IsBusy;

    /// <summary>Combines <see cref="SaveDirectory"/> (falling back to the configured default
    /// download directory) and <see cref="FileName"/> (falling back to the URL-derived name) into
    /// the single filesystem path the engine takes. Returns <c>null</c> and sets
    /// <paramref name="error"/> when the file name isn't a plain name.</summary>
    private string BuildDestination(Uri uri, out string? error)
    {
        error = null;

        var directory = string.IsNullOrWhiteSpace(SaveDirectory)
            ? _settings.DefaultDownloadDirectory
            : SaveDirectory.Trim();

        var name = string.IsNullOrWhiteSpace(FileName)
            ? Downloader.SuggestFileName(uri)
            : FileName.Trim();

        if (name != Path.GetFileName(name))
        {
            error = "File name can't contain a folder path - set the folder in \"Save to\".";
            return null!;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "File name contains characters that aren't allowed in a file name.";
            return null!;
        }

        return Path.Combine(directory, name);
    }

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
