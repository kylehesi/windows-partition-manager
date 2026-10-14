using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Form for creating a formatted partition inside one unallocated region.</summary>
public sealed class NewPartitionViewModel : INotifyPropertyChanged
{
    private const ulong MiB = 1024 * 1024;

    private static string AutomaticLetter => Localization.Get("NewPartition_Automatic");

    private readonly IStorageOperations _operations;
    private readonly IPowerStatusProvider _power;
    private readonly Disk _disk;
    private readonly FreeRegion _region;
    private readonly IReadOnlySet<char> _lettersInUse;
    private readonly Func<string, bool> _confirm;

    private string _sizeText;
    private string _fileSystem = "NTFS";
    private string _label = string.Empty;
    private string _selectedLetter;
    private bool _quickFormat = true;
    private bool _isBusy;
    private bool _isDone;
    private string _status = string.Empty;
    private string? _error;

    public NewPartitionViewModel(IStorageOperations operations, IPowerStatusProvider power, Disk disk, FreeRegion region, IReadOnlySet<char> lettersInUse, Func<string, bool> confirm)
    {
        _operations = operations;
        _power = power;
        _disk = disk;
        _region = region;
        _lettersInUse = lettersInUse;
        _confirm = confirm;

        MaximumSizeBytes = region.SizeBytes;
        _sizeText = (MaximumSizeBytes / MiB).ToString(CultureInfo.InvariantCulture) + " MB";

        Letters = [AutomaticLetter, .. Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !lettersInUse.Contains(c)).Select(c => $"{c}:")];
        _selectedLetter = Letters.Count > 1 ? Letters[1] : AutomaticLetter;

        CreateCommand = new RelayCommand(() => _ = CreateAsync(), () => CanCreate);
        UseAllCommand = new RelayCommand(() => SizeText = (MaximumSizeBytes / MiB).ToString(CultureInfo.InvariantCulture) + " MB");
        Revalidate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after a partition was created so the main window can refresh.</summary>
    public event EventHandler<Partition>? Completed;

    /// <summary>Raised after every attempt, successful or not, so the disk list can be refreshed.</summary>
    public event EventHandler? AttemptFinished;

    public string Title => Localization.Format("NewPartition_Title", _disk.Number);

    public string RegionText => Localization.Format("NewPartition_Region", _disk.FriendlyName, ByteSize.Format(_region.OffsetBytes), ByteSize.Format(_region.SizeBytes));

    public ulong MaximumSizeBytes { get; }

    public IReadOnlyList<string> FileSystems { get; } = PartitionPlanner.SupportedFileSystems;

    public IReadOnlyList<string> Letters { get; }

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public RelayCommand CreateCommand { get; }

    public RelayCommand UseAllCommand { get; }

    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (SetField(ref _sizeText, value))
            {
                Revalidate();
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
                OnPropertyChanged(nameof(CanCreate));
                OnPropertyChanged(nameof(IsFormEnabled));
                CreateCommand.RaiseCanExecuteChanged();
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
                OnPropertyChanged(nameof(CanCreate));
                OnPropertyChanged(nameof(IsFormEnabled));
                CreateCommand.RaiseCanExecuteChanged();
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

    public bool CanCreate => !IsBusy && !IsDone && !HasBlockingIssue;

    public string Summary { get; private set; } = string.Empty;

    private CreatePartitionRequest? BuildRequest(out string? parseError)
    {
        parseError = null;
        if (!ByteSize.TryParse(SizeText, out var size) || size == 0)
        {
            parseError = Localization.Format("Common_BadSize", SizeText);
            return null;
        }

        char? letter = SelectedLetter == AutomaticLetter ? null : SelectedLetter[0];
        return PartitionPlanner.Normalize(new CreatePartitionRequest
        {
            DiskNumber = _disk.Number,
            OffsetBytes = _region.OffsetBytes,
            SizeBytes = size,
            FileSystem = FileSystem,
            Label = Label.Trim(),
            DriveLetter = letter,
            QuickFormat = QuickFormat,
            ExpectedDisk = DiskFingerprint.Of(_disk),
        });
    }

    private void Revalidate()
    {
        Issues.Clear();
        var request = BuildRequest(out var parseError);
        if (request is null)
        {
            Issues.Add(new ValidationIssue(IssueSeverity.Error, parseError!));
        }
        else
        {
            foreach (var issue in PartitionPlanner.Validate(_disk, request, _lettersInUse))
            {
                Issues.Add(issue);
            }

            foreach (var issue in SafetyChecks.Power(_power.GetPowerStatus(), touchesSystemVolume: false))
            {
                Issues.Add(issue);
            }

            var labelPart = request.Label.Length > 0 ? $" \"{request.Label}\"" : string.Empty;
            var letterPart = request.DriveLetter is { } l
                ? Localization.Format("NewPartition_AsLetter", $"{l}:")
                : Localization.Get("NewPartition_AutomaticLetter");
            Summary = Localization.Format(
                "NewPartition_Summary",
                ByteSize.Format(request.SizeBytes),
                request.FileSystem,
                labelPart,
                ByteSize.Format(request.OffsetBytes),
                _disk.Number,
                letterPart);
            OnPropertyChanged(nameof(Summary));
        }

        OnPropertyChanged(nameof(HasBlockingIssue));
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.RaiseCanExecuteChanged();
    }

    private async Task CreateAsync()
    {
        var request = BuildRequest(out _);
        if (request is null || HasBlockingIssue || IsBusy)
        {
            return;
        }

        var confirm = Localization.Format("NewPartition_ConfirmBody", _disk.Number, _disk.FriendlyName);
        if (!_confirm(Summary + "\n\n" + confirm))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var created = await _operations.CreatePartitionAsync(request, progress);
            Status = Localization.Format(
                "NewPartition_Done",
                created.DriveLetter,
                created.Volume?.FileSystem,
                ByteSize.Format(created.SizeBytes),
                created.Volume?.Label is { Length: > 0 } label ? $" \"{label}\"" : string.Empty);
            IsDone = true;
            Completed?.Invoke(this, created);
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
