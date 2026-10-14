using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Form for shrinking or extending one partition, optionally creating a new partition in the space a shrink frees.</summary>
public sealed class ResizePartitionViewModel : INotifyPropertyChanged
{
    private const ulong MiB = 1024 * 1024;

    private readonly IStorageOperations _operations;
    private readonly ISupportedSizeProvider _limitsProvider;
    private readonly IPowerStatusProvider _power;
    private readonly Disk _disk;
    private readonly Partition _partition;
    private readonly IReadOnlySet<char> _lettersInUse;
    private readonly Func<string, bool> _confirm;

    private SupportedSize? _limits;
    private string _newSizeText;
    private bool _isBusy;
    private bool _isDone;
    private bool _limitsLoading = true;
    private bool _isShrink;
    private bool _createAfterShrink;
    private string _newFileSystem = "NTFS";
    private string _newLabel = string.Empty;
    private string _selectedLetter;
    private bool _newQuickFormat = true;
    private string _status = string.Empty;
    private string? _error;

    private static string AutomaticLetter => Localization.Get("NewPartition_Automatic");

    public ResizePartitionViewModel(
        IStorageOperations operations,
        ISupportedSizeProvider limitsProvider,
        IPowerStatusProvider power,
        Disk disk,
        Partition partition,
        IReadOnlySet<char> lettersInUse,
        Func<string, bool> confirm)
    {
        _operations = operations;
        _limitsProvider = limitsProvider;
        _power = power;
        _disk = disk;
        _partition = partition;
        _lettersInUse = lettersInUse;
        _confirm = confirm;

        _newSizeText = ToMegabytes(partition.SizeBytes);

        Letters = [AutomaticLetter, .. Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !lettersInUse.Contains(c)).Select(c => $"{c}:")];
        _selectedLetter = Letters.Count > 1 ? Letters[1] : AutomaticLetter;

        ResizeCommand = new RelayCommand(() => _ = ResizeAsync(), () => CanResize);
        ShrinkToMinimumCommand = new RelayCommand(() => NewSizeText = ToMegabytesRoundedUp(LowerBound));
        ExtendToMaximumCommand = new RelayCommand(() => NewSizeText = ToMegabytes(UpperBound), () => UpperBound > partition.SizeBytes);
        Revalidate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<Partition>? Completed;

    /// <summary>Raised after every attempt, successful or not, so the disk list can be refreshed.</summary>
    public event EventHandler? AttemptFinished;

    public string Title => Localization.Format(
        "Resize_Title",
        _partition.DriveLetter is { } l ? $"{l}:" : Localization.Format("Segment_Partition", _partition.Number),
        _disk.Number);

    public string InfoText
    {
        get
        {
            var volume = _partition.Volume;
            var used = volume is null ? Localization.Get("Common_Unknown") : ByteSize.Format(volume.UsedBytes);
            var label = volume?.Label is { Length: > 0 } s ? $" \"{s}\"" : string.Empty;
            return Localization.Format("Resize_Info", _disk.FriendlyName, volume?.FileSystem, label, ByteSize.Format(_partition.SizeBytes), used);
        }
    }

    public string LimitsText
    {
        get
        {
            if (_limitsLoading)
            {
                return Localization.Get("Resize_LimitsLoading");
            }

            var lower = ByteSize.Format(LowerBound);
            var upper = ByteSize.Format(UpperBound);
            var source = _limits is null ? Localization.Get("Resize_LimitsUnreported") : string.Empty;
            return UpperBound > _partition.SizeBytes
                ? Localization.Format("Resize_PossibleRange", lower, upper) + source
                : Localization.Format("Resize_CanShrinkOnly", lower) + source;
        }
    }

    /// <summary>Smallest size we will offer: the data must fit and Windows must agree.</summary>
    public ulong LowerBound => Math.Max(ResizePlanner.MinimumSize(_partition), _limits?.MinimumBytes ?? 0);

    /// <summary>Largest size we will offer: contiguous free space after the partition, capped by Windows.</summary>
    public ulong UpperBound
    {
        get
        {
            var max = ResizePlanner.MaximumSize(_disk, _partition);
            return _limits is { } l ? Math.Min(max, Math.Max(l.MaximumBytes, _partition.SizeBytes)) : max;
        }
    }

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public RelayCommand ResizeCommand { get; }

    public RelayCommand ShrinkToMinimumCommand { get; }

    public RelayCommand ExtendToMaximumCommand { get; }

    public IReadOnlyList<string> FileSystems { get; } = PartitionPlanner.SupportedFileSystems;

    public IReadOnlyList<string> Letters { get; }

    public string NewSizeText
    {
        get => _newSizeText;
        set
        {
            if (SetField(ref _newSizeText, value))
            {
                Revalidate();
            }
        }
    }

    public string ChangeText { get; private set; } = string.Empty;

    public bool CreateAfterShrink
    {
        get => _createAfterShrink;
        set
        {
            if (SetField(ref _createAfterShrink, value))
            {
                OnPropertyChanged(nameof(IsCreateFormEnabled));
                OnPropertyChanged(nameof(ActionText));
                Revalidate();
            }
        }
    }

    public string NewFileSystem
    {
        get => _newFileSystem;
        set
        {
            if (SetField(ref _newFileSystem, value))
            {
                Revalidate();
            }
        }
    }

    public string NewLabel
    {
        get => _newLabel;
        set
        {
            if (SetField(ref _newLabel, value))
            {
                Revalidate();
            }
        }
    }

    public string SelectedLetter
    {
        get => _selectedLetter;
        set
        {
            if (SetField(ref _selectedLetter, value))
            {
                Revalidate();
            }
        }
    }

    public bool NewQuickFormat
    {
        get => _newQuickFormat;
        set
        {
            if (SetField(ref _newQuickFormat, value))
            {
                Revalidate();
            }
        }
    }

    /// <summary>True when the current size is a shrink, so the create-after-shrink fields apply.</summary>
    public bool IsCreateApplicable => _isShrink;

    public bool IsCreateFormEnabled => CreateAfterShrink && _isShrink;

    public string ActionText => CreateAfterShrink && _isShrink ? Localization.Get("Resize_ActionCombined") : Localization.Get("Resize_Action");

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanResize));
                OnPropertyChanged(nameof(IsFormEnabled));
                ResizeCommand.RaiseCanExecuteChanged();
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
                OnPropertyChanged(nameof(CanResize));
                OnPropertyChanged(nameof(IsFormEnabled));
                ResizeCommand.RaiseCanExecuteChanged();
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

    public bool CanResize => !IsBusy && !IsDone && !_limitsLoading && !HasBlockingIssue;

    /// <summary>Fetches Windows' own min/max; slow (seconds), so the form opens first and fills in later.</summary>
    public async Task LoadLimitsAsync()
    {
        try
        {
            _limits = await _limitsProvider.GetSupportedSizeAsync(_disk.Number, _partition.Number);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _limits = null;
        }
        finally
        {
            _limitsLoading = false;
            OnPropertyChanged(nameof(LimitsText));
            OnPropertyChanged(nameof(LowerBound));
            OnPropertyChanged(nameof(UpperBound));
            ExtendToMaximumCommand.RaiseCanExecuteChanged();
            Revalidate();
        }
    }

    private static string ToMegabytes(ulong bytes) => (bytes / MiB).ToString(CultureInfo.InvariantCulture) + " MB";

    /// <summary>For lower bounds: rounding down would land just below the limit and be refused.</summary>
    private static string ToMegabytesRoundedUp(ulong bytes) => ((bytes + MiB - 1) / MiB).ToString(CultureInfo.InvariantCulture) + " MB";

    private ResizePartitionRequest? BuildRequest(out string? parseError)
    {
        parseError = null;
        if (!ByteSize.TryParse(NewSizeText, out var size) || size == 0)
        {
            parseError = Localization.Format("Common_BadSize", NewSizeText);
            return null;
        }

        return ResizePlanner.Normalize(new ResizePartitionRequest
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _partition.Number,
            NewSizeBytes = size,
            ExpectedDisk = DiskFingerprint.Of(_disk),
            ExpectedPartition = PartitionFingerprint.Of(_partition),
        });
    }

    private ShrinkAndCreateRequest? BuildCombinedRequest(out string? parseError)
    {
        parseError = null;
        if (!ByteSize.TryParse(NewSizeText, out var size) || size == 0)
        {
            parseError = Localization.Format("Common_BadSize", NewSizeText);
            return null;
        }

        char? letter = SelectedLetter == AutomaticLetter ? null : SelectedLetter[0];
        return ShrinkAndCreatePlanner.Normalize(new ShrinkAndCreateRequest
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _partition.Number,
            NewSizeBytes = size,
            FileSystem = NewFileSystem,
            Label = NewLabel.Trim(),
            DriveLetter = letter,
            QuickFormat = NewQuickFormat,
            ExpectedDisk = DiskFingerprint.Of(_disk),
            ExpectedPartition = PartitionFingerprint.Of(_partition),
        });
    }

    private void Revalidate()
    {
        Issues.Clear();
        _isShrink = false;

        if (CreateAfterShrink)
        {
            var combined = BuildCombinedRequest(out var combinedError);
            if (combined is null)
            {
                Issues.Add(new ValidationIssue(IssueSeverity.Error, combinedError!));
                ChangeText = string.Empty;
            }
            else
            {
                _isShrink = combined.NewSizeBytes < _partition.SizeBytes;
                foreach (var issue in ShrinkAndCreatePlanner.Validate(_disk, _partition, combined, _limits, _lettersInUse))
                {
                    Issues.Add(issue);
                }

                foreach (var issue in SafetyChecks.Power(_power.GetPowerStatus(), touchesSystemVolume: _partition.IsBoot || _partition.IsSystem))
                {
                    Issues.Add(issue);
                }

                ChangeText = _isShrink
                    ? Localization.Format("Resize_ShrinkAndCreateChange", ByteSize.Format(_partition.SizeBytes), ByteSize.Format(combined.NewSizeBytes), ByteSize.Format(_partition.SizeBytes - combined.NewSizeBytes))
                    : Localization.Get("Resize_NoChange");
            }
        }
        else
        {
            var request = BuildRequest(out var parseError);
            if (request is null)
            {
                Issues.Add(new ValidationIssue(IssueSeverity.Error, parseError!));
                ChangeText = string.Empty;
            }
            else
            {
                _isShrink = request.NewSizeBytes < _partition.SizeBytes;
                foreach (var issue in ResizePlanner.Validate(_disk, _partition, request, _limits))
                {
                    Issues.Add(issue);
                }

                foreach (var issue in SafetyChecks.Power(_power.GetPowerStatus(), touchesSystemVolume: _partition.IsBoot || _partition.IsSystem))
                {
                    Issues.Add(issue);
                }

                ChangeText = request.NewSizeBytes < _partition.SizeBytes
                    ? Localization.Format("Resize_ShrinkChange", ByteSize.Format(_partition.SizeBytes), ByteSize.Format(request.NewSizeBytes), ByteSize.Format(_partition.SizeBytes - request.NewSizeBytes))
                    : request.NewSizeBytes > _partition.SizeBytes
                        ? Localization.Format("Resize_ExtendChange", ByteSize.Format(_partition.SizeBytes), ByteSize.Format(request.NewSizeBytes), ByteSize.Format(request.NewSizeBytes - _partition.SizeBytes))
                        : Localization.Get("Resize_NoChange");
            }
        }

        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(IsCreateApplicable));
        OnPropertyChanged(nameof(IsCreateFormEnabled));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(HasBlockingIssue));
        OnPropertyChanged(nameof(CanResize));
        ResizeCommand.RaiseCanExecuteChanged();
    }

    private async Task ResizeAsync()
    {
        if (CreateAfterShrink)
        {
            await ResizeAndCreateAsync();
            return;
        }

        var request = BuildRequest(out _);
        if (request is null || HasBlockingIssue || IsBusy)
        {
            return;
        }

        if (!_confirm(ChangeText + "\n\n" + Localization.Get("Resize_ConfirmBody")))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var result = await _operations.ResizePartitionAsync(request, new Progress<string>(s => Status = s));
            Status = Localization.Format("Resize_Done", ByteSize.Format(result.SizeBytes)) +
                     (request.NewSizeBytes < _partition.SizeBytes ? Localization.Get("Resize_DoneHint") : string.Empty);
            IsDone = true;
            Completed?.Invoke(this, result);
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Status = Localization.Get("Resize_Failed");
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResizeAndCreateAsync()
    {
        var combined = BuildCombinedRequest(out _);
        if (combined is null || !_isShrink || HasBlockingIssue || IsBusy)
        {
            return;
        }

        if (!_confirm(ChangeText + "\n\n" + Localization.Get("Resize_ShrinkAndCreateConfirmBody")))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var shrunk = await _operations.ResizePartitionAsync(new ResizePartitionRequest
            {
                DiskNumber = combined.DiskNumber,
                PartitionNumber = combined.PartitionNumber,
                NewSizeBytes = combined.NewSizeBytes,
                ExpectedDisk = combined.ExpectedDisk,
                ExpectedPartition = combined.ExpectedPartition,
            }, progress);

            var create = ShrinkAndCreatePlanner.CreateRequest(combined, _disk, _partition)!;
            await _operations.CreatePartitionAsync(create, progress);

            Status = Localization.Format("Resize_ShrinkAndCreateDone", ByteSize.Format(shrunk.SizeBytes));
            IsDone = true;
            Completed?.Invoke(this, shrunk);
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Status = Localization.Get("Resize_Failed");
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
