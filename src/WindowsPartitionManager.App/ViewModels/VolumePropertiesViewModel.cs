using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Form for changing a partition's drive letter and its volume label. Neither touches the data.</summary>
public sealed class VolumePropertiesViewModel : INotifyPropertyChanged
{
    private static string NoLetter => Localization.Get("Properties_NoLetter");

    private readonly IStorageOperations _operations;
    private readonly Disk _disk;
    private readonly Partition _partition;
    private readonly IReadOnlySet<char> _lettersInUse;
    private readonly Func<string, bool> _confirm;
    private readonly string _currentLetter;
    private readonly string _currentLabel;

    private string _selectedLetter;
    private string _label;
    private bool _isBusy;
    private bool _isDone;
    private string _status = string.Empty;
    private string? _error;

    public VolumePropertiesViewModel(IStorageOperations operations, Disk disk, Partition partition, IReadOnlySet<char> lettersInUse, Func<string, bool> confirm)
    {
        _operations = operations;
        _disk = disk;
        _partition = partition;
        _lettersInUse = lettersInUse;
        _confirm = confirm;

        var current = partition.DriveLetter ?? partition.Volume?.DriveLetter;
        _currentLetter = current is { } c ? $"{char.ToUpperInvariant(c)}:" : NoLetter;
        _currentLabel = partition.Volume?.Label ?? string.Empty;
        _selectedLetter = _currentLetter;
        _label = _currentLabel;

        CanChangeLetter = DriveLetterPlanner.CanChange(disk, partition);
        CanRename = LabelPlanner.CanRename(disk, partition);
        LabelMaxLength = partition.Volume is { } volume ? LabelPlanner.MaximumLength(volume.FileSystem) : 32;

        // The current letter, "no letter" and every free letter, in alphabetical order.
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Where(l => !lettersInUse.Contains(l)).Select(l => $"{l}:");
        Letters = [NoLetter, .. free.Append(_currentLetter).Where(l => l != NoLetter).Distinct().Order(StringComparer.Ordinal)];

        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => CanApply);
        Revalidate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every attempt, successful or not, so the disk list can be refreshed.</summary>
    public event EventHandler? AttemptFinished;

    public string Title => Localization.Format("Properties_Title", PartitionName, _disk.Number);

    public string InfoText => Localization.Format(
        "Properties_Info",
        _disk.FriendlyName,
        _partition.Volume?.FileSystem ?? CoreText.PartitionType(_partition.TypeDescription),
        ByteSize.Format(_partition.SizeBytes));

    public IReadOnlyList<string> Letters { get; }

    public bool CanChangeLetter { get; }

    public bool CanRename { get; }

    public int LabelMaxLength { get; }

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public RelayCommand ApplyCommand { get; }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnStateChanged();
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
                OnStateChanged();
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

    public string Summary { get; private set; } = string.Empty;

    public bool CanApply => !IsBusy && !IsDone && HasChanges && !Issues.Any(i => i.IsError);

    private string PartitionName => _partition.DriveLetter is { } l ? $"{l}:" : Localization.Format("Segment_Partition", _partition.Number);

    private bool LetterChanged => CanChangeLetter && SelectedLetter != _currentLetter;

    private bool LabelChanged => CanRename && !string.Equals(Label.Trim(), _currentLabel, StringComparison.Ordinal);

    private bool HasChanges => LetterChanged || LabelChanged;

    private ChangeDriveLetterRequest LetterRequest() => new()
    {
        DiskNumber = _disk.Number,
        PartitionNumber = _partition.Number,
        NewDriveLetter = SelectedLetter == NoLetter ? null : SelectedLetter[0],
        ExpectedDisk = DiskFingerprint.Of(_disk),
        ExpectedPartition = PartitionFingerprint.Of(_partition),
    };

    private SetLabelRequest LabelRequest() => new()
    {
        DiskNumber = _disk.Number,
        PartitionNumber = _partition.Number,
        Label = Label.Trim(),
        ExpectedDisk = DiskFingerprint.Of(_disk),
        ExpectedPartition = PartitionFingerprint.Of(_partition),
    };

    private void Revalidate()
    {
        Issues.Clear();
        var changes = new List<string>();

        if (LabelChanged)
        {
            foreach (var issue in LabelPlanner.Validate(_disk, _partition, LabelRequest()))
            {
                Issues.Add(issue);
            }

            changes.Add(Localization.Format("Properties_RenameTo", Label.Trim().Length > 0 ? Label.Trim() : Localization.Get("Properties_EmptyLabel")));
        }

        if (LetterChanged)
        {
            var request = LetterRequest();
            foreach (var issue in DriveLetterPlanner.Validate(_disk, _partition, request, _lettersInUse))
            {
                Issues.Add(issue);
            }

            changes.Add(request.NewDriveLetter is { } l
                ? Localization.Format("Properties_LetterTo", l)
                : Localization.Get("Properties_RemoveLetter"));
        }

        Summary = changes.Count == 0
            ? Localization.Get(CanChangeLetter ? "Properties_NoChanges" : "Properties_LetterLocked")
            : Localization.Format("Properties_Summary", PartitionName, string.Join(Localization.Get("Properties_And"), changes));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private async Task ApplyAsync()
    {
        if (!CanApply || !_confirm(Summary + "\n\n" + Localization.Get("Properties_ConfirmBody")))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            // The label first: renaming needs the volume, not the letter, and cannot fail halfway.
            if (LabelChanged)
            {
                Status = Localization.Get("Properties_Renaming");
                await _operations.SetLabelAsync(LabelRequest());
            }

            if (LetterChanged)
            {
                Status = Localization.Get("Properties_ChangingLetter");
                await _operations.ChangeDriveLetterAsync(LetterRequest());
            }

            Status = Localization.Get("Properties_Done");
            IsDone = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Status = Localization.Get("Properties_Failed");
        }
        finally
        {
            IsBusy = false;
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(IsFormEnabled));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.RaiseCanExecuteChanged();
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
