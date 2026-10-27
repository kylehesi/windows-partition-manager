using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>The three ways Windows checks a file system (Repair-Volume; chkdsk underneath).</summary>
public enum CheckMode
{
    /// <summary>Online scan (chkdsk /scan): the volume stays usable, problems are only recorded. NTFS only.</summary>
    Scan,

    /// <summary>Fixes what a scan recorded; the volume is unavailable for a few seconds (chkdsk /spotfix). NTFS only.</summary>
    SpotFix,

    /// <summary>Full check and repair with the volume dismounted (chkdsk /f); the only option for FAT and exFAT.</summary>
    OfflineScanAndFix,
}

/// <summary>How a check ended, grouped from the Repair-Volume status codes.</summary>
public enum CheckOutcome
{
    Clean,
    Fixed,
    NeedsSpotFix,
    NeedsOfflineFix,
    Retry,
    Failed,
}

public sealed record CheckResult(uint Code, CheckOutcome Outcome)
{
    /// <summary>Maps Repair-Volume's RepairStatus (Volume.cdxml in Windows' Storage module) to an outcome.</summary>
    public static CheckResult FromCode(uint code) => new(code, code switch
    {
        0 or 4 or 17 => CheckOutcome.Clean,
        1 or 2 or 5 or 16 => CheckOutcome.Fixed,
        6 or 7 or 15 => CheckOutcome.NeedsSpotFix,
        11 or 13 => CheckOutcome.NeedsOfflineFix,
        8 or 9 or 10 or 12 or 18 => CheckOutcome.Retry,
        _ => CheckOutcome.Failed,
    });

    public string Message => CoreText.Get($"Check_Outcome_{Outcome}");
}

public sealed record CheckVolumeRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    public required CheckMode Mode { get; init; }

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}

public static class CheckPlanner
{
    /// <summary>The checks Windows offers for the volume's file system; empty when it cannot be checked.</summary>
    public static IReadOnlyList<CheckMode> Available(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);

        if (partition.Kind != PartitionKind.Basic || partition.Volume is not { } volume || disk.IsOffline)
        {
            return [];
        }

        if (volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            // A fix on the running Windows volume needs a restart; that stays with chkdsk itself.
            return partition.IsBoot || partition.IsSystem || disk.IsReadOnly
                ? [CheckMode.Scan]
                : [CheckMode.Scan, CheckMode.SpotFix, CheckMode.OfflineScanAndFix];
        }

        var fat = volume.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
                  || volume.FileSystem.Equals("FAT", StringComparison.OrdinalIgnoreCase)
                  || volume.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
        return fat && !disk.IsReadOnly ? [CheckMode.OfflineScanAndFix] : [];
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, Partition partition, CheckVolumeRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

        if (request.DiskNumber != disk.Number || request.PartitionNumber != partition.Number)
        {
            Error(CoreText.Get("Common_RequestMismatch"));
            return issues;
        }

        if (partition.Volume is { } encrypted && SafetyChecks.Encryption(encrypted, EncryptionUse.Contents) is { Count: > 0 } locked)
        {
            issues.AddRange(locked);
            return issues;
        }

        if (Available(disk, partition).Contains(request.Mode))
        {
            var letter = partition.DriveLetter is { } l ? $"{char.ToUpperInvariant(l)}:" : CoreText.Get("Safety_TheVolume");
            switch (request.Mode)
            {
                case CheckMode.SpotFix:
                    Warn(CoreText.Format("Check_SpotFixWarning", letter));
                    break;
                case CheckMode.OfflineScanAndFix:
                    Warn(CoreText.Format("Check_OfflineWarning", letter));
                    break;
            }

            return issues;
        }

        if (disk.IsOffline)
        {
            Error(CoreText.Get("Common_DiskOffline"));
        }
        else if (partition.Kind != PartitionKind.Basic || partition.Volume is null)
        {
            Error(CoreText.Get("Letter_NoFileSystem"));
        }
        else if (request.Mode != CheckMode.Scan && (partition.IsBoot || partition.IsSystem))
        {
            var letter = partition.DriveLetter is { } l ? $"{char.ToUpperInvariant(l)}:" : "C:";
            Error(CoreText.Format("Check_WindowsVolumeFix", letter));
        }
        else if (request.Mode != CheckMode.Scan && disk.IsReadOnly)
        {
            Error(CoreText.Get("Common_DiskReadOnly"));
        }
        else
        {
            Error(CoreText.Format("Check_NotSupported", partition.Volume.FileSystem));
        }

        return issues;
    }
}
