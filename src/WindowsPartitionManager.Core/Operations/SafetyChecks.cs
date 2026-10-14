using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Snapshot of the machine's power source.</summary>
/// <param name="OnAcPower">True when plugged in, false on battery, null when unknown (desktop without a battery reports true).</param>
/// <param name="BatteryPercent">0..100, or null when unknown.</param>
public sealed record PowerStatus(bool? OnAcPower, int? BatteryPercent);

/// <summary>
/// Pre-flight rules shared by every write operation. None of these protect against a bug in
/// Windows; they keep us from starting an operation in conditions where a power loss or a
/// pre-existing file system problem would turn a recoverable interruption into a bad day.
/// </summary>
public static class SafetyChecks
{
    /// <summary>Below this charge we refuse to write to any partition table.</summary>
    public const int MinimumBatteryPercent = 30;

    public static IReadOnlyList<ValidationIssue> Power(PowerStatus? status, bool touchesSystemVolume)
    {
        if (status is null || status.OnAcPower != false)
        {
            return [];
        }

        var percent = status.BatteryPercent;
        var charge = percent is { } p ? $" ({p} %)" : string.Empty;

        if (percent is { } low && low < MinimumBatteryPercent)
        {
            return [new ValidationIssue(IssueSeverity.Error, CoreText.Format("Safety_BatteryRefused", charge))];
        }

        if (touchesSystemVolume)
        {
            return [new ValidationIssue(IssueSeverity.Error, CoreText.Format("Safety_BatterySystemRefused", charge))];
        }

        return [new ValidationIssue(IssueSeverity.Warning, CoreText.Format("Safety_BatteryWarning", charge))];
    }

    /// <summary>Warnings about the disk itself: removable media can be yanked mid-operation.</summary>
    public static IReadOnlyList<ValidationIssue> Disk(Model.Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        if (disk.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            disk.BusType.Equals("SD", StringComparison.OrdinalIgnoreCase) ||
            disk.BusType.Equals("MMC", StringComparison.OrdinalIgnoreCase))
        {
            return [new ValidationIssue(IssueSeverity.Warning, CoreText.Format("Safety_Removable", disk.Number, disk.BusType))];
        }

        return [];
    }

    public static IReadOnlyList<ValidationIssue> VolumeHealth(Volume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var letter = volume.DriveLetter is { } l ? $"{l}:" : CoreText.Get("Safety_TheVolume");
        var issues = new List<ValidationIssue>();

        if (volume.IsDirty)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, CoreText.Format("Safety_Dirty", letter)));
        }

        if (volume.Health is Model.VolumeHealth.ScanNeeded or Model.VolumeHealth.SpotFixNeeded or Model.VolumeHealth.FullRepairNeeded)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, CoreText.Format("Safety_NeedsRepair", letter, Describe(volume.Health))));
        }

        return issues;
    }

    private static string Describe(VolumeHealth health) => health switch
    {
        Model.VolumeHealth.ScanNeeded => CoreText.Get("Safety_ScanNeeded"),
        Model.VolumeHealth.SpotFixNeeded => CoreText.Get("Safety_SpotFixNeeded"),
        Model.VolumeHealth.FullRepairNeeded => CoreText.Get("Safety_FullRepairNeeded"),
        _ => health.ToString(),
    };
}
