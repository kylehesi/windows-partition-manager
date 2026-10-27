using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>One choice in the optimize form; the first one is what suits the drive.</summary>
public sealed record OptimizeModeOption(OptimizeMode Mode, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>Form for TRIM or defragmentation of one volume. Nothing on it changes; it stays usable.</summary>
public sealed class OptimizeVolumeViewModel : INotifyPropertyChanged
{
    private readonly IStorageOperations _operations;
    private readonly Disk _disk;
    private readonly Partition _partition;
    private readonly MediaType _media;

    private OptimizeModeOption _selected;
    private bool _isBusy;
    private string _status = string.Empty;
    private string? _error;
    private string _result = string.Empty;

    public OptimizeVolumeViewModel(IStorageOperations operations, Disk disk, Partition partition, MediaType media)
    {
        _operations = operations;
        _disk = disk;
        _partition = partition;
        _media = media;

        var available = OptimizePlanner.Available(disk, partition, media);
        Modes = available
            .Select((m, i) => new OptimizeModeOption(
                m,
                Localization.Get($"Optimize_Mode{m}") + (i == 0 && media != MediaType.Unknown ? " " + Localization.Get("Optimize_Recommended") : string.Empty),
                Localization.Get($"Optimize_Mode{m}_Text")))
            .ToList();
        _selected = Modes[0];

        RunCommand = new RelayCommand(() => _ = RunAsync(), () => CanRun);
        Revalidate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => Localization.Format("Optimize_Title", PartitionName, _disk.Number);

    public string InfoText => $"{_disk.FriendlyName} · {Localization.Get($"HealthWin_Media{_media}")} · {_partition.Volume?.FileSystem} · {ByteSize.Format(_partition.SizeBytes)}";

    public IReadOnlyList<OptimizeModeOption> Modes { get; }

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public RelayCommand RunCommand { get; }

    public OptimizeModeOption Selected
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

    private string PartitionName => _partition.DriveLetter is { } l ? $"{l}:" : Localization.Format("Segment_Partition", _partition.Number);

    private OptimizeVolumeRequest Request() => new()
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
        foreach (var issue in OptimizePlanner.Validate(_disk, _partition, Request(), _media))
        {
            Issues.Add(issue);
        }

        OnPropertyChanged(nameof(CanRun));
        RunCommand.RaiseCanExecuteChanged();
    }

    /// <summary>No confirmation: nothing on the volume changes and it stays usable throughout.</summary>
    private async Task RunAsync()
    {
        if (!CanRun)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Result = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _operations.OptimizeVolumeAsync(Request(), new Progress<string>(s => Status = s));
            Result = Localization.Format("Optimize_Done", Selected.Name, stopwatch.Elapsed.TotalSeconds.ToString("F0", CultureInfo.CurrentCulture));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
        }
        finally
        {
            Status = string.Empty;
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
