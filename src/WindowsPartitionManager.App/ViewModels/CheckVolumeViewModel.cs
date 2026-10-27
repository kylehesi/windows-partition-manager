using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>One choice in the check form, with what it does in plain words.</summary>
public sealed record CheckModeOption(CheckMode Mode, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>Form for checking (and optionally repairing) one volume's file system.</summary>
public sealed class CheckVolumeViewModel : INotifyPropertyChanged
{
    private readonly IStorageOperations _operations;
    private readonly Disk _disk;
    private readonly Partition _partition;
    private readonly Func<string, bool> _confirm;

    private CheckModeOption _selected;
    private bool _isBusy;
    private string _status = string.Empty;
    private string? _error;
    private string _result = string.Empty;
    private bool _resultIsGood;

    public CheckVolumeViewModel(IStorageOperations operations, Disk disk, Partition partition, Func<string, bool> confirm)
    {
        _operations = operations;
        _disk = disk;
        _partition = partition;
        _confirm = confirm;

        Modes = CheckPlanner.Available(disk, partition)
            .Select(m => new CheckModeOption(m, Localization.Get($"Check_Mode{m}"), Localization.Get($"Check_Mode{m}_Text")))
            .ToList();
        _selected = Modes[0];

        RunCommand = new RelayCommand(() => _ = RunAsync(), () => CanRun);
        Revalidate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every check so the disk list can show the volume's state again.</summary>
    public event EventHandler? AttemptFinished;

    public string Title => Localization.Format("Check_Title", PartitionName, _disk.Number);

    public string InfoText => $"{_disk.FriendlyName} · {_partition.Volume?.FileSystem} · {ByteSize.Format(_partition.SizeBytes)}"
                              + (_partition.IsBoot ? " · " + Localization.Get("Check_WindowsVolumeNote") : string.Empty);

    public IReadOnlyList<CheckModeOption> Modes { get; }

    /// <summary>On the Windows drive only the scan is offered; this says how a repair is done there.</summary>
    public string WindowsVolumeHint => _partition.IsBoot || _partition.IsSystem
        ? CoreText.Format("Check_WindowsVolumeFix", _partition.DriveLetter is { } l ? $"{l}:" : "C:")
        : string.Empty;

    public bool HasWindowsVolumeHint => WindowsVolumeHint.Length > 0;

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public RelayCommand RunCommand { get; }

    public CheckModeOption Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
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
                OnPropertyChanged(nameof(IsFormEnabled));
                OnPropertyChanged(nameof(CanRun));
                RunCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsFormEnabled => !IsBusy;

    public bool CanRun => !IsBusy && !Issues.Any(i => i.IsError);

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

    public string Result
    {
        get => _result;
        private set
        {
            if (SetField(ref _result, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => Result.Length > 0;

    /// <summary>Green for clean or fixed, amber when more needs doing.</summary>
    public bool ResultIsGood
    {
        get => _resultIsGood;
        private set => SetField(ref _resultIsGood, value);
    }

    private string PartitionName => _partition.DriveLetter is { } l ? $"{l}:" : Localization.Format("Segment_Partition", _partition.Number);

    private CheckVolumeRequest Request() => new()
    {
        DiskNumber = _disk.Number,
        PartitionNumber = _partition.Number,
        Mode = Selected.Mode,
        ExpectedDisk = DiskFingerprint.Of(_disk),
        ExpectedPartition = PartitionFingerprint.Of(_partition),
    };

    private void Revalidate()
    {
        Issues.Clear();
        foreach (var issue in CheckPlanner.Validate(_disk, _partition, Request()))
        {
            Issues.Add(issue);
        }

        OnPropertyChanged(nameof(CanRun));
        RunCommand.RaiseCanExecuteChanged();
    }

    private async Task RunAsync()
    {
        // A scan changes nothing, so it needs no confirmation; the fixes do.
        var warnings = string.Join("\n\n", Issues.Select(i => i.Message));
        if (!CanRun || (Selected.Mode != CheckMode.Scan && !_confirm(Localization.Format("Check_Confirm", Selected.Name, PartitionName) + (warnings.Length > 0 ? "\n\n" + warnings : string.Empty))))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Result = string.Empty;
        try
        {
            var result = await _operations.CheckVolumeAsync(Request(), new Progress<string>(s => Status = s));
            ResultIsGood = result.Outcome is CheckOutcome.Clean or CheckOutcome.Fixed;
            Result = result.Message;
            Status = string.Empty;

            // Offer the next step straight away.
            var next = result.Outcome switch
            {
                CheckOutcome.NeedsSpotFix => Modes.FirstOrDefault(m => m.Mode == CheckMode.SpotFix),
                CheckOutcome.NeedsOfflineFix => Modes.FirstOrDefault(m => m.Mode == CheckMode.OfflineScanAndFix),
                _ => null,
            };
            if (next is not null)
            {
                Selected = next;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Status = string.Empty;
        }
        finally
        {
            IsBusy = false;
            AttemptFinished?.Invoke(this, EventArgs.Empty);
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
