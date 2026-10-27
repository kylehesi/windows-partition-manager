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
    private readonly IVirtualDiskOperations _virtualDisks;
    private readonly IDiskHealthProvider _health;
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
        IVirtualDiskOperations virtualDisks,
        IDiskHealthProvider health,
        Func<string, bool> confirm)
    {
        _storage = storage;
        _inspector = inspector;
        _supportedSizes = supportedSizes;
        _operations = operations;
        _power = power;
        _unblock = unblock;
        _virtualDisks = virtualDisks;
        _health = health;
        _confirm = confirm;

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        AnalyzeCommand = new RelayCommand<SegmentRow>(row => AnalysisRequested?.Invoke(this, CreateAnalysis(row)), row => row.CanAnalyze);
        NewPartitionCommand = new RelayCommand<SegmentRow>(row => NewPartitionRequested?.Invoke(this, CreateNewPartition(row)), row => row.CanCreate);
        ResizeCommand = new RelayCommand<SegmentRow>(row => ResizeRequested?.Invoke(this, CreateResize(row)), row => row.CanResize);
        DeleteCommand = new RelayCommand<SegmentRow>(row => DestructiveRequested?.Invoke(this, CreateDestructive(row, isFormat: false)), row => row.CanDelete);
        FormatCommand = new RelayCommand<SegmentRow>(row => DestructiveRequested?.Invoke(this, CreateDestructive(row, isFormat: true)), row => row.CanFormat);
        HealthCommand = new RelayCommand<Disk>(disk => HealthRequested?.Invoke(this, new DiskHealthViewModel(_health, disk)));
        CreateVirtualDiskCommand = new RelayCommand(() => CreateVirtualDiskRequested?.Invoke(this, CreateVirtualDisk()));
        AttachVirtualDiskCommand = new RelayCommand(() => AttachVirtualDiskRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        DetachVirtualDiskCommand = new RelayCommand<Disk>(disk => _ = DetachVirtualDiskAsync(disk), _ => !IsBusy);
        DiskStateCommand = new RelayCommand<DiskStateTarget>(target => _ = SetDiskStateAsync(target), _ => !IsBusy);
        OptimizeCommand = new RelayCommand<SegmentRow>(row => _ = OpenOptimizeAsync(row), row => row.CanOptimize);
        CheckCommand = new RelayCommand<SegmentRow>(row => CheckRequested?.Invoke(this, CreateCheck(row)), row => row.CanCheck);
        PropertiesCommand = new RelayCommand<SegmentRow>(row => PropertiesRequested?.Invoke(this, CreateProperties(row)), row => row.CanEditProperties);
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
                foreach (var command in new[] { AnalyzeCommand, ResizeCommand, NewPartitionCommand, DeleteCommand, FormatCommand, InitializeCommand, PropertiesCommand, CheckCommand, OptimizeCommand })
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

    public RelayCommand<DiskStateTarget> DiskStateCommand { get; }

    /// <summary>Raised when the user asks how a drive is doing; the view opens the window and loads it.</summary>
    public event EventHandler<DiskHealthViewModel>? HealthRequested;

    public RelayCommand<Disk> HealthCommand { get; }

    /// <summary>Raised when the user wants a new VHDX; the view opens the form.</summary>
    public event EventHandler<CreateVirtualDiskViewModel>? CreateVirtualDiskRequested;

    /// <summary>Raised when the user wants to attach a VHD/VHDX; the view asks for the file and calls <see cref="AttachVirtualDiskAsync"/>.</summary>
    public event EventHandler? AttachVirtualDiskRequested;

    public RelayCommand CreateVirtualDiskCommand { get; }

    public RelayCommand AttachVirtualDiskCommand { get; }

    public RelayCommand<Disk> DetachVirtualDiskCommand { get; }

    private CreateVirtualDiskViewModel CreateVirtualDisk()
    {
        var form = new CreateVirtualDiskViewModel(_virtualDisks, _confirm);
        form.AttemptFinished += (_, _) => _ = RefreshAsync();
        return form;
    }

    public async Task AttachVirtualDiskAsync(string path)
    {
        IsBusy = true;
        Error = null;
        Status = Localization.Get("Vhd_Attaching");
        try
        {
            await _virtualDisks.AttachAsync(path);
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

    /// <summary>Detaches a virtual disk after a confirmation; the file stays where it is.</summary>
    private async Task DetachVirtualDiskAsync(Disk disk)
    {
        var request = new DetachVirtualDiskRequest { DiskNumber = disk.Number, ExpectedDisk = DiskFingerprint.Of(disk) };
        var issues = VirtualDiskPlanner.ValidateDetach(disk, request);
        if (issues.Any(i => i.IsError))
        {
            Error = string.Join(" ", issues.Where(i => i.IsError).Select(i => i.Message));
            return;
        }

        var prompt = Localization.Format("DiskAction_Detach_Prompt", disk.Number, disk.VirtualDiskPath);
        var warnings = string.Join("\n\n", issues.Select(i => i.Message));
        if (!_confirm(warnings.Length > 0 ? prompt + "\n\n" + warnings : prompt))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Status = Localization.Format("DiskAction_Working", disk.Number);
        try
        {
            await _virtualDisks.DetachAsync(request);
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

    /// <summary>
    /// Takes a disk offline or online, or switches its read-only attribute. Fully reversible,
    /// so a confirmation that lists the consequences is enough.
    /// </summary>
    private async Task SetDiskStateAsync(DiskStateTarget target)
    {
        var disk = target.Disk;
        var request = new SetDiskStateRequest { DiskNumber = disk.Number, Change = target.Change, ExpectedDisk = DiskFingerprint.Of(disk) };
        var issues = DiskStatePlanner.Validate(disk, request);
        if (issues.Any(i => i.IsError))
        {
            Error = string.Join(" ", issues.Where(i => i.IsError).Select(i => i.Message));
            return;
        }

        var prompt = Localization.Format($"DiskAction_{target.Change}_Prompt", disk.Number, disk.FriendlyName);
        var warnings = string.Join("\n\n", issues.Select(i => i.Message));
        if (!_confirm(warnings.Length > 0 ? prompt + "\n\n" + warnings : prompt))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Status = Localization.Format("DiskAction_Working", disk.Number);
        try
        {
            await _operations.SetDiskStateAsync(request);
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

    /// <summary>Raised when the user wants to TRIM or defragment a volume; the view opens the form.</summary>
    public event EventHandler<OptimizeVolumeViewModel>? OptimizeRequested;

    public RelayCommand<SegmentRow> OptimizeCommand { get; }

    /// <summary>The media type decides which optimization is recommended, so it is read before the form opens.</summary>
    private async Task OpenOptimizeAsync(SegmentRow row)
    {
        var media = MediaType.Unknown;
        try
        {
            media = (await _health.GetHealthAsync(row.DiskNumber))?.MediaType ?? MediaType.Unknown;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without the media type both choices are still offered, just without a recommendation.
        }

        OptimizeRequested?.Invoke(this, new OptimizeVolumeViewModel(_operations, row.Disk, row.Partition!, media));
    }

    /// <summary>Raised when the user wants to check a file system; the view opens the form.</summary>
    public event EventHandler<CheckVolumeViewModel>? CheckRequested;

    public RelayCommand<SegmentRow> CheckCommand { get; }

    private CheckVolumeViewModel CreateCheck(SegmentRow row)
    {
        var form = new CheckVolumeViewModel(_operations, row.Disk, row.Partition!, _confirm);
        form.AttemptFinished += (_, _) => _ = RefreshAsync();
        return form;
    }

    /// <summary>Raised when the user wants to change a drive letter or a label; the view opens the form.</summary>
    public event EventHandler<VolumePropertiesViewModel>? PropertiesRequested;

    public RelayCommand<SegmentRow> PropertiesCommand { get; }

    private VolumePropertiesViewModel CreateProperties(SegmentRow row)
    {
        var form = new VolumePropertiesViewModel(_operations, row.Disk, row.Partition!, LettersInUse(), _confirm);
        form.AttemptFinished += (_, _) => _ = RefreshAsync();
        return form;
    }

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
