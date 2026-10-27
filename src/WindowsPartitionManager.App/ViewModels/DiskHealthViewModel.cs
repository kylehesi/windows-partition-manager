using System.ComponentModel;
using System.Globalization;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>One labelled value of the health window.</summary>
public sealed record HealthValue(string Label, string Value);

/// <summary>What a drive reports about its health, with a plain verdict. Read-only.</summary>
public sealed class DiskHealthViewModel : INotifyPropertyChanged
{
    private readonly IDiskHealthProvider _provider;
    private readonly Disk _disk;
    private bool _isLoading = true;
    private HealthVerdict _verdict = HealthVerdict.Unknown;
    private IReadOnlyList<HealthValue> _values = [];
    private IReadOnlyList<HealthFinding> _findings = [];
    private string? _error;

    public DiskHealthViewModel(IDiskHealthProvider provider, Disk disk)
    {
        _provider = provider;
        _disk = disk;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => Localization.Format("HealthWin_Title", _disk.Number);

    public string DiskText => $"{_disk.FriendlyName} · {ByteSize.Format(_disk.SizeBytes)} · {_disk.BusType}";

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value, nameof(IsLoading));
    }

    public HealthVerdict Verdict
    {
        get => _verdict;
        private set
        {
            Set(ref _verdict, value, nameof(Verdict));
            OnPropertyChanged(nameof(VerdictText));
        }
    }

    public string VerdictText => IsLoading ? Localization.Get("HealthWin_Loading") : Localization.Get($"HealthWin_Verdict{Verdict}");

    public IReadOnlyList<HealthValue> Values
    {
        get => _values;
        private set => Set(ref _values, value, nameof(Values));
    }

    public IReadOnlyList<HealthFinding> Findings
    {
        get => _findings;
        private set => Set(ref _findings, value, nameof(Findings));
    }

    public string? Error
    {
        get => _error;
        private set
        {
            Set(ref _error, value, nameof(Error));
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error is not null;

    public async Task LoadAsync()
    {
        try
        {
            var health = await _provider.GetHealthAsync(_disk.Number);
            if (health is null)
            {
                Findings = [new HealthFinding(HealthVerdict.Unknown, Localization.Get("HealthWin_NoPhysicalDisk"))];
                return;
            }

            var (verdict, findings) = DiskHealthAssessment.Assess(health);
            Values = Describe(health);
            Findings = findings;
            Verdict = verdict;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(VerdictText));
        }
    }

    private static List<HealthValue> Describe(DiskHealth health)
    {
        var notReported = Localization.Get("HealthWin_NotReported");
        string Show<T>(T? value, Func<T, string> format) where T : struct => value is { } v ? format(v) : notReported;
        string Count(ulong value) => value.ToString("N0", CultureInfo.CurrentCulture);

        var values = new List<HealthValue>
        {
            new(Localization.Get("HealthWin_Type"), Localization.Get($"HealthWin_Media{health.MediaType}")),
            new(Localization.Get("HealthWin_WindowsStatus"), Localization.Get($"HealthWin_Status{health.Health}")),
        };

        if (!health.CountersAvailable)
        {
            values.Add(new(Localization.Get("HealthWin_Counters"), Localization.Get("HealthWin_CountersMissing")));
            return values;
        }

        var temperature = Show(health.TemperatureCelsius, t => $"{t} °C");
        if (health.TemperatureMaxCelsius is { } max)
        {
            temperature += " " + Localization.Format("HealthWin_TemperatureMax", max);
        }

        values.Add(new(Localization.Get("HealthWin_Temperature"), temperature));
        if (health.MediaType != MediaType.Hdd)
        {
            values.Add(new(Localization.Get("HealthWin_Wear"), Show(health.WearPercent, w => $"{w} %")));
        }

        values.Add(new(Localization.Get("HealthWin_PowerOn"), Show(health.PowerOnHours, h => Localization.Format("HealthWin_Hours", Count(h), Math.Round(h / 24.0 / 365.0, 1).ToString(CultureInfo.CurrentCulture)))));
        values.Add(new(Localization.Get("HealthWin_ReadErrors"), Errors(health.ReadErrorsTotal, health.ReadErrorsUncorrected)));
        values.Add(new(Localization.Get("HealthWin_WriteErrors"), Errors(health.WriteErrorsTotal, health.WriteErrorsUncorrected)));
        return values;

        string Errors(ulong? total, ulong? uncorrected) => total is null && uncorrected is null
            ? notReported
            : Localization.Format("HealthWin_Errors", total is { } t ? Count(t) : notReported, uncorrected is { } u ? Count(u) : notReported);
    }

    private void Set<T>(ref T field, T value, string propertyName)
    {
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
