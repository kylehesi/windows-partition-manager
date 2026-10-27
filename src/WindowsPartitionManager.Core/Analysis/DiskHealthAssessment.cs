using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Analysis;

public enum HealthVerdict
{
    /// <summary>Nothing the drive reports gives a reason for concern.</summary>
    Good,

    /// <summary>Worth watching: warm, well-worn, or Windows reports a warning.</summary>
    Attention,

    /// <summary>Back up now: uncorrected errors, worn out, or Windows reports the drive unhealthy.</summary>
    Bad,

    /// <summary>The drive reports too little to judge.</summary>
    Unknown,
}

public sealed record HealthFinding(HealthVerdict Verdict, string Message);

/// <summary>
/// Turns the raw counters into a plain verdict. Deliberately cautious: SMART cannot predict every
/// failure, so a good verdict never claims more than "nothing reported points to a problem".
/// </summary>
public static class DiskHealthAssessment
{
    /// <summary>Above this an SSD throttles and an HDD ages faster, unless the drive reports its own limit.</summary>
    public const int SsdHotCelsius = 70;

    public const int HddHotCelsius = 55;

    public static (HealthVerdict Verdict, IReadOnlyList<HealthFinding> Findings) Assess(DiskHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        var findings = new List<HealthFinding>();
        void Add(HealthVerdict verdict, string message) => findings.Add(new HealthFinding(verdict, message));

        switch (health.Health)
        {
            case HealthStatus.Unhealthy:
                Add(HealthVerdict.Bad, CoreText.Get("Health_WindowsUnhealthy"));
                break;
            case HealthStatus.Warning:
                Add(HealthVerdict.Attention, CoreText.Get("Health_WindowsWarning"));
                break;
        }

        var uncorrected = (health.ReadErrorsUncorrected ?? 0) + (health.WriteErrorsUncorrected ?? 0);
        if (uncorrected > 0)
        {
            Add(HealthVerdict.Bad, CoreText.Format("Health_UncorrectedErrors", uncorrected));
        }

        if (health.WearPercent is { } wear)
        {
            if (wear >= 90)
            {
                Add(HealthVerdict.Bad, CoreText.Format("Health_WornOut", wear));
            }
            else if (wear >= 70)
            {
                Add(HealthVerdict.Attention, CoreText.Format("Health_Worn", wear));
            }
        }

        if (health.TemperatureCelsius is { } temperature and > 0)
        {
            var limit = health.TemperatureMaxCelsius is { } max and > 0
                ? max
                : health.MediaType == MediaType.Hdd ? HddHotCelsius : SsdHotCelsius;
            if (temperature >= limit)
            {
                Add(HealthVerdict.Attention, CoreText.Format("Health_Hot", temperature, limit));
            }
        }

        if (findings.Count == 0)
        {
            var knowsEnough = health.CountersAvailable || health.Health == HealthStatus.Healthy;
            return knowsEnough
                ? (HealthVerdict.Good, [new HealthFinding(HealthVerdict.Good, CoreText.Get(health.CountersAvailable ? "Health_AllGood" : "Health_GoodWithoutCounters"))])
                : (HealthVerdict.Unknown, [new HealthFinding(HealthVerdict.Unknown, CoreText.Get("Health_NoData"))]);
        }

        // Bad outranks Attention; findings are never Unknown here.
        return (findings.Any(f => f.Verdict == HealthVerdict.Bad) ? HealthVerdict.Bad : HealthVerdict.Attention, findings);
    }
}
