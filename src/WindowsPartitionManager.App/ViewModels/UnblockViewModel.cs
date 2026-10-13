using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Operations;
<<<<<<< HEAD
using WindowsPartitionManager.Platform.Windows;
=======
<<<<<<< HEAD
using WindowsPartitionManager.Platform.Windows;
=======
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
>>>>>>> 12-oct-2026

namespace WindowsPartitionManager.App.ViewModels;

public sealed class UnblockStepViewModel(UnblockStep step) : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public UnblockStep Step { get; } = step;

    public string Title => Step.Title;

    public string Description => Step.Description;

    public string Details
    {
        get
        {
<<<<<<< HEAD
            var parts = new List<string> { Localization.Format("Unblock_PastTarget", ByteSize.Format(Step.BytesPastTarget)) };
            if (Step.RequiresReboot) parts.Add(Localization.Get("Unblock_NeedsReboot"));
            parts.Add(Step.Reversible ? Localization.Get("Unblock_CanUndo") : Localization.Get("Unblock_CannotUndo"));
=======
            var parts = new List<string> { $"{ByteSize.Format(Step.BytesPastTarget)} past the target" };
            if (Step.RequiresReboot) parts.Add("needs a reboot");
            parts.Add(Step.Reversible ? "Windows Partition Manager can undo this" : "cannot be undone");
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
            return string.Join(" · ", parts);
        }
    }

<<<<<<< HEAD
    public string FilesText => string.Join("\n", Step.Files.Take(6)) + (Step.Files.Count > 6 ? "\n" + Localization.Format("Unblock_MoreFiles", Step.Files.Count - 6) : string.Empty);
=======
    public string FilesText => string.Join("\n", Step.Files.Take(6)) + (Step.Files.Count > 6 ? $"\n… and {Step.Files.Count - 6} more" : string.Empty);
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}

/// <summary>The unblock wizard: pick which removable blockers to clear, apply, and later restore.</summary>
public sealed class UnblockViewModel : INotifyPropertyChanged
{
    private readonly UnblockRunner _runner;
    private readonly IUnblockOperations _operations;
    private readonly Func<string, bool> _confirm;
    private bool _isBusy;
    private bool _isDone;
    private string _status = string.Empty;
    private string? _error;

    public UnblockViewModel(IUnblockOperations operations, char driveLetter, IReadOnlyList<UnblockStep> steps, Func<string, bool> confirm)
    {
        _operations = operations;
        _runner = new UnblockRunner(operations);
        _confirm = confirm;
        DriveLetter = driveLetter;
        Steps = steps.Select(s => new UnblockStepViewModel(s)).ToList();
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => !IsBusy && !IsDone && Steps.Any(s => s.IsSelected));
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => !IsBusy && HasPendingRestore);

        foreach (var step in Steps)
        {
            step.PropertyChanged += (_, _) => ApplyCommand.RaiseCanExecuteChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public char DriveLetter { get; }

<<<<<<< HEAD
    public string Title => Localization.Format("Unblock_Title", DriveLetter);
=======
    public string Title => $"Unblock shrink · {DriveLetter}:";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669

    public string Summary => UnblockPlanner.Summarize(Steps.Select(s => s.Step).ToList());

    public IReadOnlyList<UnblockStepViewModel> Steps { get; }

    public bool HasSteps => Steps.Count > 0;

    public RelayCommand ApplyCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public bool HasPendingRestore => _operations.LoadState() is { HasSomethingToRestore: true };

    public string RestoreText => _operations.LoadState() is { } s
<<<<<<< HEAD
        ? $"Windows Partition Manager changed settings on {LanguagePreference.FormatTimestamp(s.AppliedAt)} for {s.Volume}: and can restore them."
=======
<<<<<<< HEAD
        ? Localization.Format("Unblock_RestoreText", LanguagePreference.FormatTimestamp(s.AppliedAt), s.Volume)
        : Localization.Get("Unblock_NoRestore");
=======
        ? $"Windows Partition Manager changed settings on {s.AppliedAt:yyyy-MM-dd HH:mm} for {s.Volume}: and can restore them."
>>>>>>> 12-oct-2026
        : "No settings to restore.";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
                RestoreCommand.RaiseCanExecuteChanged();
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
                ApplyCommand.RaiseCanExecuteChanged();
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

    private async Task ApplyAsync()
    {
        var selected = Steps.Where(s => s.IsSelected).Select(s => s.Step).ToList();
        if (selected.Count == 0 || IsBusy)
        {
            return;
        }

        var lines = string.Join("\n", selected.Select(s => "• " + s.Title));
<<<<<<< HEAD
        var irreversible = selected.Any(s => !s.Reversible) ? "\n\n" + Localization.Get("Unblock_Irreversible") : string.Empty;
        var reboot = selected.Any(s => s.RequiresReboot) ? "\n\n" + Localization.Get("Unblock_RebootNote") : string.Empty;
        if (!_confirm(Localization.Format("Unblock_ApplyTitle", DriveLetter) + "\n\n" + lines + irreversible + reboot))
=======
        var irreversible = selected.Any(s => !s.Reversible) ? "\n\nDeleting restore points cannot be undone." : string.Empty;
        var reboot = selected.Any(s => s.RequiresReboot) ? "\n\nThe page file change takes effect after a reboot." : string.Empty;
        if (!_confirm($"Apply these steps on {DriveLetter}:?\n\n{lines}{irreversible}{reboot}"))
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var state = await _runner.ApplyAsync(DriveLetter, selected.Select(s => s.Action), new Progress<string>(s => Status = s));
            Status = selected.Any(s => s.RequiresReboot)
<<<<<<< HEAD
                ? Localization.Get("Unblock_DoneReboot")
                : state.HasSomethingToRestore
                    ? Localization.Get("Unblock_DoneRestore")
                    : Localization.Get("Unblock_Done");
=======
                ? "Done. Reboot, then run the shrink. Afterwards come back here and press Restore to put the settings back."
                : state.HasSomethingToRestore
                    ? "Done. Run the shrink now; afterwards press Restore to put the settings back."
                    : "Done. Run the shrink now.";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
            IsDone = true;
            OnPropertyChanged(nameof(HasPendingRestore));
            OnPropertyChanged(nameof(RestoreText));
            RestoreCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
<<<<<<< HEAD
            Status = Localization.Get("Unblock_Failed");
=======
            Status = "Failed. Settings that were already changed are recorded and can be restored.";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
            OnPropertyChanged(nameof(HasPendingRestore));
            RestoreCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync()
    {
<<<<<<< HEAD
        if (IsBusy || !_confirm(Localization.Get("Common_RestoreConfirm")))
=======
        if (IsBusy || !_confirm("Restore hibernation and page file settings to what they were before Windows Partition Manager changed them?"))
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            await _runner.RestoreAsync(new Progress<string>(s => Status = s));
            OnPropertyChanged(nameof(HasPendingRestore));
            OnPropertyChanged(nameof(RestoreText));
            RestoreCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
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
