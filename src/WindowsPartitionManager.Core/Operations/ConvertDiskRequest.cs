using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Give an empty disk the other partition table (Set-Disk -PartitionStyle, as Disk Management does).</summary>
public sealed record ConvertDiskRequest
{
    public required int DiskNumber { get; init; }

    /// <summary><see cref="PartitionStyle.Gpt"/> or <see cref="PartitionStyle.Mbr"/>.</summary>
    public required PartitionStyle Style { get; init; }

    public DiskFingerprint? ExpectedDisk { get; init; }
}

/// <summary>
/// Converts only disks that hold no data: converting with partitions in place would mean
/// rewriting the partition table underneath them, which this program never does. The small
/// Microsoft Reserved partition Windows adds to every GPT disk holds no data and does not count.
/// </summary>
public static class ConvertDiskPlanner
{
    /// <summary>MBR addresses at most 2 TiB with 512-byte sectors.</summary>
    public const ulong MbrLimitBytes = 2UL * 1024 * 1024 * 1024 * 1024;

    /// <summary>The style the disk can be converted to, or null when it cannot be converted.</summary>
    public static PartitionStyle? Target(Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.IsSystem || disk.IsBoot || disk.IsReadOnly || disk.IsOffline || !IsEmpty(disk))
        {
            return null;
        }

        return disk.Style switch
        {
            PartitionStyle.Gpt => PartitionStyle.Mbr,
            PartitionStyle.Mbr => PartitionStyle.Gpt,
            _ => null, // a RAW disk is initialized, not converted
        };
    }

    public static bool IsEmpty(Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return disk.Partitions.All(p => p.Kind == PartitionKind.MicrosoftReserved);
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, ConvertDiskRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));

        if (request.DiskNumber != disk.Number || request.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr))
        {
            Error(CoreText.Get("Common_RequestMismatch"));
            return issues;
        }

        if (disk.IsSystem || disk.IsBoot)
        {
            Error(CoreText.Get("Convert_WindowsDisk"));
            return issues;
        }

        if (disk.IsReadOnly)
        {
            Error(CoreText.Get("Common_DiskReadOnly"));
        }

        if (disk.IsOffline)
        {
            Error(CoreText.Get("Common_DiskOffline"));
        }

        if (disk.Style == request.Style)
        {
            Error(CoreText.Format("Convert_AlreadyStyle", request.Style.ToString().ToUpperInvariant()));
        }
        else if (disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown)
        {
            Error(CoreText.Get("Convert_NotInitialized"));
        }

        var data = disk.Partitions.Count(p => p.Kind != PartitionKind.MicrosoftReserved);
        if (data > 0)
        {
            Error(CoreText.Format("Convert_HasPartitions", data));
        }

        if (request.Style == PartitionStyle.Mbr && disk.SizeBytes > MbrLimitBytes)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, CoreText.Get("Convert_MbrLimit")));
        }

        issues.AddRange(SafetyChecks.Disk(disk));
        return issues;
    }
}
