using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IStorageProvider _storage;
    private readonly IVolumeInspector _inspector;
    private readonly ISupportedSizeProvider _supportedSizes;
    private readonly IStorageOperations _operations;
    private readonly IPowerStatusProvider _power;
    private readonly IUnblockOperations _unblock;
    private readonly Func<string, bool> _confirm;
    private bool _isBusy;
    private string _status = Localization.Get("Main_Ready");
    private string? _error;
    private SegmentRow? _selectedRow;

    public MainViewModel(
        IStorageProvider storage,
        IVolumeInspector inspector,
        ISupportedSizeProvider supportedSizes,
        IStorageOperations operations,
        IPowerStatusProvider power,
        IUnblockOperations unblock,
        Func<string, bool> confirm)
    {
        _storage = storage;
        _inspector = inspector;
        _supportedSizes = supportedSizes;
        _operations = operations;
        _power = power;
        _unblock = unblock;
        _confirm = confirm;

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        AnalyzeCommand = new RelayCommand<SegmentRow>(row => AnalysisRequested?.Invoke(this, CreateAnalysis(row)), row => row.CanAnalyze);
        NewPartitionCommand = new RelayCommand<SegmentRow>(row => NewPartitionRequested?.Invoke(this, CreateNewPartition(row)), row => row.CanCreate);
        ResizeCommand = new RelayCommand<SegmentRow>(row => ResizeRequested?.Invoke(this, CreateResize(row)), row => row.CanResize);
        DeleteCommand = new RelayCommand<SegmentRow>(row => DestructiveRequested?.Invoke(this, CreateDestructive(row, isFormat: false)), row => row.CanDelete);
        FormatCommand = new RelayCommand<SegmentRow>(row => DestructiveRequested?.Invoke(this, CreateDestructive(row, isFormat: true)), row => row.CanFormat);
        InitializeCommand = new RelayCommand<SegmentRow>(row => _ = InitializeAsync(row), row => row.CanInitialize && !IsBusy);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Raised when the user opens Settings; the view shows the window.</summary>
    public event EventHandler? SettingsRequested;

    public RelayCommand SettingsCommand { get; }

    /// <summary>The row the toolbar acts on. Set by whichever disk list the user clicked in.</summary>
    public SegmentRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetField(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(SelectionText));
                foreach (var command in new[] { AnalyzeCommand, ResizeCommand, NewPartitionCommand, DeleteCommand, FormatCommand, InitializeCommand })
                {
                    command.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public string SelectionText => SelectedRow is { } row
        ? Localization.Format("Main_SelectionSelected", row.Name, row.DiskNumber)
        : Localization.Get("Main_SelectionNone");

    /// <summary>Raised when the user wants to delete or format a partition; the view opens the typed-confirmation form.</summary>
    public event EventHandler<DestructiveActionViewModel>? DestructiveRequested;

    public RelayCommand<SegmentRow> DeleteCommand { get; }

    public RelayCommand<SegmentRow> FormatCommand { get; }

    public RelayCommand<SegmentRow> InitializeCommand { get; }

    private DestructiveActionViewModel CreateDestructive(SegmentRow row, bool isFormat)
    {
        var form = new DestructiveActionViewModel(_operations, _power, row.Disk, row.Partition!, isFormat);
        form.AttemptFinished += (_, _) => _ = RefreshAsync();
        return form;
    }

    /// <summary>Writes a GPT partition table to a disk that has none. Nothing to lose, so a plain confirmation is enough.</summary>
    private async Task InitializeAsync(SegmentRow row)
    {
        var disk = row.Disk;
        var prompt = Localization.Format("Main_InitializePrompt", disk.Number, disk.FriendlyName, Core.Formatting.ByteSize.Format(disk.SizeBytes))
                     + "\n\n" + Localization.Get("Main_InitializePromptBody");
        if (!_confirm(prompt))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Status = Localization.Format("Main_Initializing", disk.Number);
        try
        {
            await _operations.InitializeDiskAsync(disk.Number, PartitionStyle.Gpt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    /// <summary>Raised when the user wants to shrink or extend a partition; the view opens the form.</summary>
    public event EventHandler<ResizePartitionViewModel>? ResizeRequested;

    public RelayCommand<SegmentRow> ResizeCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user asks for a shrink analysis; the view opens a window for it.</summary>
    public event EventHandler<ShrinkAnalysisViewModel>? AnalysisRequested;

    /// <summary>Raised when the user wants a partition in a free region; the view opens the form.</summary>
    public event EventHandler<NewPartitionViewModel>? NewPartitionRequested;

    public ObservableCollection<DiskViewModel> Disks { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand<SegmentRow> AnalyzeCommand { get; }

    public RelayCommand<SegmentRow> NewPartitionCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

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

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Status = Localization.Get("Main_ReadingDisks");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var disks = await _storage.GetDisksAsync();

            SelectedRow = null;
            Disks.Clear();
            foreach (var disk in disks)
            {
                Disks.Add(new DiskViewModel(disk, this));
            }

            Status = Localization.Format("Main_Refreshed", disks.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Rows from an older read must not stay clickable.
            SelectedRow = null;
            Disks.Clear();
            Error = ex.Message;
            Status = Localization.Get("Main_ReadFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ShrinkAnalysisViewModel CreateAnalysis(SegmentRow row)
        => new(_inspector, _supportedSizes, _unblock, _confirm, row.DriveLetter!.Value, row.DiskNumber, row.PartitionNumber!.Value);

    private NewPartitionViewModel CreateNewPartition(SegmentRow row)
    {
        var region = new FreeRegion(row.Segment.OffsetBytes, row.Segment.SizeBytes);
        var form = new NewPartitionViewModel(_operations, _power, row.Disk, region, LettersInUse(), _confirm);
        form.AttemptFinished += (_, _) => _ = RefreshAsync();
        return form;
    }

    private ResizePartitionViewModel CreateResize(SegmentRow row)
    {
        var form = new ResizePartitionViewModel(_operations, _supportedSizes, _power, row.Disk, row.Partition!, LettersInUse(), _confirm);
        form.AttemptFinished += (_, _) => _ = RefreshAsync();
        return form;
    }

    /// <summary>Drive letters already taken by partitions or mounted volumes, so a new one is never offered twice.</summary>
    private HashSet<char> LettersInUse()
    {
        // Optical, network and substituted drives hold letters too, not only partitions.
        var letters = new HashSet<char>(PartitionPlanner.LettersInUse(Disks.Select(d => d.Disk)));
        foreach (var root in Environment.GetLogicalDrives())
        {
            if (root.Length > 0 && char.IsLetter(root[0]))
            {
                letters.Add(char.ToUpperInvariant(root[0]));
            }
        }

        return letters;
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
