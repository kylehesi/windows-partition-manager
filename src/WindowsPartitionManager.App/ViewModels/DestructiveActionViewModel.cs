using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>
/// Shared form for the two operations that destroy data on an existing partition: delete and
/// format. The user has to type the partition's name before the button becomes active.
/// </summary>
public sealed class DestructiveActionViewModel : INotifyPropertyChanged
{
    private readonly IStorageOperations _operations;
    private readonly IPowerStatusProvider _power;
    private readonly Disk _disk;
    private readonly Partition _partition;
    private string _confirmationText = string.Empty;
    private string _fileSystem = "NTFS";
    private string _label;
    private bool _quickFormat = true;
    private bool _isBusy;
    private bool _isDone;
    private string _status = string.Empty;
    private string? _error;

    public DestructiveActionViewModel(IStorageOperations operations, IPowerStatusProvider power, Disk disk, Partition partition, bool isFormat)
    {
        _operations = operations;
        _power = power;
        _disk = disk;
        _partition = partition;
        IsFormat = isFormat;
        _label = partition.Volume?.Label ?? string.Empty;

        ExecuteCommand = new RelayCommand(() => _ = ExecuteAsync(), () => CanExecute);
        Revalidate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? Completed;

    /// <summary>Raised after every attempt, successful or not, so the disk list can be refreshed.</summary>
    public event EventHandler? AttemptFinished;

    public bool IsFormat { get; }

    public string PartitionName => _partition.DriveLetter is { } l ? $"{l}:" : Localization.Format("Segment_Partition", _partition.Number);

    public string Title => IsFormat
        ? Localization.Format("Destructive_FormatTitle", PartitionName, _disk.Number)
        : Localization.Format("Destructive_DeleteTitle", PartitionName, _disk.Number);

    public string InfoText
    {
        get
        {
            var volume = _partition.Volume;
            var fs = volume is null ? _partition.TypeDescription : volume.FileSystem;
            var label = volume?.Label is { Length: > 0 } s ? $" \"{s}\"" : string.Empty;
            var used = volume is null ? string.Empty : Localization.Format("Destructive_InUse", ByteSize.Format(volume.UsedBytes));
            return $"{_disk.FriendlyName} ({_disk.BusType}) · {fs}{label} · {ByteSize.Format(_partition.SizeBytes)}{used}";
        }
    }

    public string Explanation => IsFormat
        ? Localization.Get("Destructive_FormatExplanation")
        : Localization.Get("Destructive_DeleteExplanation");

    public string ConfirmationPrompt => Localization.Format("Destructive_ConfirmPrompt", PartitionName);

    public IReadOnlyList<string> FileSystems { get; } = PartitionPlanner.SupportedFileSystems;

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public RelayCommand ExecuteCommand { get; }

    public string ActionText => IsFormat ? Localization.Get("Destructive_FormatAction") : Localization.Get("Destructive_DeleteAction");

    public string ConfirmationText
    {
        get => _confirmationText;
        set
        {
            if (SetField(ref _confirmationText, value))
            {
                OnPropertyChanged(nameof(CanExecute));
                ExecuteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileSystem
    {
        get => _fileSystem;
        set
        {
            if (SetField(ref _fileSystem, value))
            {
                Revalidate();
            }
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            if (SetField(ref _label, value))
            {
                Revalidate();
            }
        }
    }

    public bool QuickFormat
    {
        get => _quickFormat;
        set
        {
            if (SetField(ref _quickFormat, value))
            {
                Revalidate();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(IsFormEnabled));
                ExecuteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDone
    {
        get => _isDone;
        private set
        {
            if (SetField(ref _isDone, value))
            {
                OnPropertyChanged(nameof(CanExecute));
                OnPropertyChanged(nameof(IsFormEnabled));
                ExecuteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsFormEnabled => !IsBusy && !IsDone;

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public bool HasBlockingIssue => Issues.Any(i => i.IsError);

    public bool ConfirmationMatches => string.Equals(ConfirmationText.Trim(), PartitionName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ConfirmationText.Trim(), PartitionName.TrimEnd(':'), StringComparison.OrdinalIgnoreCase);

    public bool CanExecute => !IsBusy && !IsDone && !HasBlockingIssue && ConfirmationMatches;

    private FormatPartitionRequest FormatRequest => new()
    {
        DiskNumber = _disk.Number,
        PartitionNumber = _partition.Number,
        FileSystem = FileSystem,
        Label = Label.Trim(),
        QuickFormat = QuickFormat,
        ExpectedDisk = DiskFingerprint.Of(_disk),
        ExpectedPartition = PartitionFingerprint.Of(_partition),
    };

    private void Revalidate()
    {
        Issues.Clear();
        var issues = IsFormat
            ? FormatPlanner.Validate(_disk, _partition, FormatRequest)
            : PartitionPlanner.ValidateDelete(_disk, _partition);
        foreach (var issue in issues.Concat(SafetyChecks.Power(_power.GetPowerStatus(), touchesSystemVolume: false)))
        {
            Issues.Add(issue);
        }

        OnPropertyChanged(nameof(HasBlockingIssue));
        OnPropertyChanged(nameof(CanExecute));
        ExecuteCommand.RaiseCanExecuteChanged();
    }

    private async Task ExecuteAsync()
    {
        if (!CanExecute)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            if (IsFormat)
            {
                var result = await _operations.FormatPartitionAsync(FormatRequest, new Progress<string>(s => Status = s));
                Status = Localization.Format("Destructive_DoneFormat", result.DriveLetter, result.Volume?.FileSystem, result.Volume?.Label is { Length: > 0 } l ? $" \"{l}\"" : string.Empty);
            }
            else
            {
                Status = Localization.Get("Destructive_Deleting");
                await _operations.DeletePartitionAsync(new DeletePartitionRequest
                {
                    DiskNumber = _disk.Number,
                    PartitionNumber = _partition.Number,
                    ExpectedDisk = DiskFingerprint.Of(_disk),
                    ExpectedPartition = PartitionFingerprint.Of(_partition),
                });
                Status = Localization.Get("Destructive_Deleted");
            }

            IsDone = true;
            Completed?.Invoke(this, EventArgs.Empty);
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Status = Localization.Get("NewPartition_Failed");
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
