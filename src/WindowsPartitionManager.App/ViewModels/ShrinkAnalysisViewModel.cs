using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

public sealed class ShrinkAnalysisViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IVolumeInspector _inspector;
    private readonly ISupportedSizeProvider _supportedSizes;
    private readonly IUnblockOperations _unblock;
    private readonly Func<string, bool> _confirm;
    private readonly int _diskNumber;
    private IReadOnlyList<UnblockStep> _unblockSteps = [];
    private readonly int _partitionNumber;
    private CancellationTokenSource? _cancellation;

    private string _targetText = string.Empty;
<<<<<<< HEAD
    private string _status = Localization.Get("ShrinkAnalysis_Start");
=======
    private string _status = "Press Analyze to start.";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
    private string _progressText = string.Empty;
    private double _progressFraction;
    private bool _progressIndeterminate;
    private bool _isBusy;
    private string _reportText = string.Empty;
    private string? _error;

    public ShrinkAnalysisViewModel(
        IVolumeInspector inspector,
        ISupportedSizeProvider supportedSizes,
        IUnblockOperations unblock,
        Func<string, bool> confirm,
        char driveLetter,
        int diskNumber,
        int partitionNumber)
    {
        _inspector = inspector;
        _supportedSizes = supportedSizes;
        _unblock = unblock;
        _confirm = confirm;
        _diskNumber = diskNumber;
        _partitionNumber = partitionNumber;
        DriveLetter = driveLetter;

        AnalyzeCommand = new RelayCommand(() => _ = AnalyzeAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
        UnblockCommand = new RelayCommand(
            () => UnblockRequested?.Invoke(this, new UnblockViewModel(_unblock, DriveLetter, _unblockSteps, _confirm)),
            () => !IsBusy && _unblockSteps.Count > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user opens the unblock wizard; the view shows a window for it.</summary>
    public event EventHandler<UnblockViewModel>? UnblockRequested;

    public RelayCommand UnblockCommand { get; }

    public string UnblockText => _unblockSteps.Count == 0 ? string.Empty : UnblockPlanner.Summarize(_unblockSteps);

    public char DriveLetter { get; }

<<<<<<< HEAD
    public string Title => Localization.Format("ShrinkAnalysis_Title", DriveLetter);
=======
    public string Title => $"Shrink analysis · {DriveLetter}:";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669

    public RelayCommand AnalyzeCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Wanted volume size, e.g. "300GB". Empty means "used data + 10 % headroom".</summary>
    public string TargetText
    {
        get => _targetText;
        set => SetField(ref _targetText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    public double ProgressFraction
    {
        get => _progressFraction;
        private set => SetField(ref _progressFraction, value);
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => SetField(ref _progressIndeterminate, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                AnalyzeCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                UnblockCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ReportText
    {
        get => _reportText;
        private set => SetField(ref _reportText, value);
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

    /// <summary>Cancels a running analysis; called when the window closes.</summary>
    public void Dispose()
    {
        _cancellation?.Cancel();
    }

    public async Task AnalyzeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ulong? target = null;
        if (!string.IsNullOrWhiteSpace(TargetText))
        {
            if (!ByteSize.TryParse(TargetText, out var parsed))
            {
<<<<<<< HEAD
                Error = Localization.Format("Common_BadSize", TargetText);
=======
                Error = $"Could not understand the size '{TargetText}'. Try something like 300GB or 1.5TB.";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
                return;
            }

            target = parsed;
        }

        IsBusy = true;
        Error = null;
        ReportText = string.Empty;
        _unblockSteps = [];
        OnPropertyChanged(nameof(UnblockText));
        UnblockCommand.RaiseCanExecuteChanged();
        ProgressIndeterminate = true;
        ProgressFraction = 0;
<<<<<<< HEAD
        Status = Localization.Get("ShrinkAnalysis_Opening");
=======
        Status = "Opening volume…";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
        var stopwatch = Stopwatch.StartNew();

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        var progress = new Progress<InspectProgress>(p =>
        {
            Status = p.Stage + "…";
            if (p.Total > 0)
            {
                ProgressIndeterminate = false;
                ProgressFraction = (double)p.Done / p.Total;
                ProgressText = $"{p.Done:N0} / {p.Total:N0}";
            }
            else
            {
                ProgressIndeterminate = true;
                ProgressText = p.Done > 0 ? p.Done.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
            }
        });

        try
        {
            var supportedTask = _supportedSizes.GetSupportedSizeAsync(_diskNumber, _partitionNumber, token);

            using var volume = await _inspector.OpenAsync(DriveLetter, token);
            var report = await ShrinkAnalyzer.AnalyzeAsync(volume, target, null, progress, token);

            // Show our result right away; Windows' own estimate is slow and only informative.
            ReportText = ShrinkReportFormatter.ToText(report, maxBlockers: 200);
<<<<<<< HEAD
            Status = Localization.Format("ShrinkAnalysis_Analysed", stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
=======
            Status = $"Analysed in {stopwatch.Elapsed.TotalSeconds:F1} s. Waiting for Windows' own shrink estimate…";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
            ProgressIndeterminate = true;

            SupportedSize? supported = null;
            try
            {
                supported = await supportedTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Leave the Windows line out of the report.
            }

            report = ShrinkAnalyzer.WithWindowsLimit(report, supported);
            ReportText = ShrinkReportFormatter.ToText(report, maxBlockers: 200);
            _unblockSteps = UnblockPlanner.Plan(report);
            OnPropertyChanged(nameof(UnblockText));
            UnblockCommand.RaiseCanExecuteChanged();
<<<<<<< HEAD
            Status = Localization.Format("ShrinkAnalysis_Done", stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException)
        {
            Status = Localization.Get("ShrinkAnalysis_Cancelled");
        }
        catch (UnauthorizedAccessException ex)
        {
            Error = ex.Message + Localization.Get("ShrinkAnalysis_AdminHint");
            Status = Localization.Get("ShrinkAnalysis_Failed");
=======
            Status = $"Done in {stopwatch.Elapsed.TotalSeconds:F1} s.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (UnauthorizedAccessException ex)
        {
            Error = ex.Message + " Start Windows Partition Manager as administrator.";
            Status = "Failed.";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
<<<<<<< HEAD
            Status = Localization.Get("ShrinkAnalysis_Failed");
=======
            Status = "Failed.";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
        }
        finally
        {
            ProgressIndeterminate = false;
            ProgressFraction = 0;
            ProgressText = string.Empty;
            IsBusy = false;
            _cancellation.Dispose();
            _cancellation = null;
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
