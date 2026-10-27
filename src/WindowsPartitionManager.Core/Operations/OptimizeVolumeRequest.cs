using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>What "Optimize Drives" does to a volume (Optimize-Volume).</summary>
public enum OptimizeMode
{
    /// <summary>Tells the SSD again which blocks are free, so it can keep writes fast (Optimize-Volume -ReTrim).</summary>
    ReTrim,

    /// <summary>Moves file pieces together (Optimize-Volume -Defrag); for hard disks, and before a shrink.</summary>
    Defrag,
}

public sealed record OptimizeVolumeRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    public required OptimizeMode Mode { get; init; }

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}

public static class OptimizePlanner
{
    /// <summary>
    /// The optimizations Windows supports for the volume's file system, the recommended one first:
    /// TRIM for SSDs, defragmentation for hard disks.
    /// </summary>
    public static IReadOnlyList<OptimizeMode> Available(Disk disk, Partition partition, MediaType media = MediaType.Unknown)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);

        if (partition.Kind != PartitionKind.Basic || partition.Volume is not { } volume || disk.IsOffline || disk.IsReadOnly)
        {
            return [];
        }

        var fs = volume.FileSystem;
        if (fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return media == MediaType.Hdd ? [OptimizeMode.Defrag, OptimizeMode.ReTrim] : [OptimizeMode.ReTrim, OptimizeMode.Defrag];
        }

        if (fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
        {
            return [OptimizeMode.ReTrim];
        }

        return fs.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || fs.Equals("FAT", StringComparison.OrdinalIgnoreCase)
            ? [OptimizeMode.Defrag]
            : [];
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, Partition partition, OptimizeVolumeRequest request, MediaType media = MediaType.Unknown)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        if (request.DiskNumber != disk.Number || request.PartitionNumber != partition.Number)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, CoreText.Get("Common_RequestMismatch")));
            return issues;
        }

        if (!Available(disk, partition, media).Contains(request.Mode))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, disk.IsReadOnly
                ? CoreText.Get("Common_DiskReadOnly")
                : disk.IsOffline
                    ? CoreText.Get("Common_DiskOffline")
                    : CoreText.Format("Optimize_NotSupported", partition.Volume?.FileSystem ?? CoreText.Get("Common_Unknown"))));
            return issues;
        }

        switch (request.Mode)
        {
            case OptimizeMode.Defrag when media == MediaType.Ssd:
                issues.Add(new ValidationIssue(IssueSeverity.Warning, CoreText.Get("Optimize_DefragOnSsd")));
                break;
            case OptimizeMode.Defrag:
                issues.Add(new ValidationIssue(IssueSeverity.Warning, CoreText.Get("Optimize_DefragTakesLong")));
                break;
            case OptimizeMode.ReTrim when media == MediaType.Hdd:
                issues.Add(new ValidationIssue(IssueSeverity.Warning, CoreText.Get("Optimize_TrimOnHdd")));
                break;
        }

        return issues;
    }
}
