using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Validation for shrinking and extending partitions, before anything is written.</summary>
public static class ResizePlanner
{
    public const ulong Alignment = FreeSpaceCalculator.DefaultAlignment;

    /// <summary>Below this much free space after a shrink, NTFS gets slow and fragmentation-prone.</summary>
    private const double ComfortableHeadroom = 0.05;

    private static readonly string[] ResizableFileSystems = ["NTFS", "ReFS"];

    public static bool CanResize(Partition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return partition.Kind == PartitionKind.Basic
               && partition.Volume is { } volume
               && ResizableFileSystems.Contains(volume.FileSystem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Aligns the new size down to 1 MiB, as Windows would.</summary>
    public static ResizePartitionRequest Normalize(ResizePartitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var aligned = request.NewSizeBytes - (request.NewSizeBytes % Alignment);
        return aligned == request.NewSizeBytes ? request : request with { NewSizeBytes = aligned };
    }

    /// <summary>The largest size the partition can grow to: its own size plus the free region that starts right after it.</summary>
    public static ulong MaximumSize(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);

        var nextStart = FreeSpaceCalculator.AlignUp(partition.EndBytes, Alignment);
        var following = FreeSpaceCalculator.Compute(disk).FirstOrDefault(r => r.OffsetBytes == nextStart);
        return following is null ? partition.SizeBytes : following.EndBytes - partition.OffsetBytes;
    }

    /// <summary>Smallest size we would even attempt: the data has to fit.</summary>
    public static ulong MinimumSize(Partition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var used = partition.Volume?.UsedBytes ?? partition.SizeBytes;
        return Math.Max(PartitionPlanner.MinimumSizeBytes, FreeSpaceCalculator.AlignUp(used, Alignment));
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, Partition partition, ResizePartitionRequest request, SupportedSize? windowsLimits = null)
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

        if (disk.IsReadOnly)
        {
            Error(CoreText.Get("Common_DiskReadOnly"));
        }

        if (disk.IsOffline)
        {
            Error(CoreText.Get("Common_DiskOffline"));
        }

        if (partition.Kind != PartitionKind.Basic)
        {
            Error(CoreText.Format("Resize_OnlyBasicData", CoreText.PartitionType(partition.TypeDescription)));
            return issues;
        }

        if (partition.Volume is not { } volume)
        {
            Error(CoreText.Get("Resize_NoFileSystem"));
            return issues;
        }

        // Before the file system test: a locked volume reports no file system at all.
        var encryption = SafetyChecks.Encryption(volume, EncryptionUse.Resize);
        if (encryption.Any(i => i.IsError))
        {
            issues.AddRange(encryption);
            return issues;
        }

        if (!ResizableFileSystems.Contains(volume.FileSystem, StringComparer.OrdinalIgnoreCase))
        {
            Error(CoreText.Format("Resize_OnlyNtfsReFS", volume.FileSystem.Length == 0 ? CoreText.Get("Common_Unknown") : volume.FileSystem));
            return issues;
        }

        issues.AddRange(SafetyChecks.VolumeHealth(volume));
        issues.AddRange(encryption);

        if (request.NewSizeBytes % Alignment != 0)
        {
            Error(CoreText.Format("Resize_NotAligned", ByteSize.Format(Alignment)));
        }

        if (request.NewSizeBytes == partition.SizeBytes)
        {
            Error(CoreText.Get("Resize_SameSize"));
            return issues;
        }

        if (request.NewSizeBytes < partition.SizeBytes)
        {
            ValidateShrink(partition, volume, request, windowsLimits, Error, Warn);
        }
        else
        {
            ValidateExtend(disk, partition, request, windowsLimits, Error);
        }

        issues.AddRange(SafetyChecks.Disk(disk));

        if (partition.IsBoot || partition.IsSystem)
        {
            Warn(CoreText.Get("Resize_SystemVolume"));
        }

        return issues;
    }

    private static void ValidateShrink(Partition partition, Volume volume, ResizePartitionRequest request, SupportedSize? limits, Action<string> error, Action<string> warn)
    {
        var floor = MinimumSize(partition);
        if (request.NewSizeBytes < PartitionPlanner.MinimumSizeBytes)
        {
            error(CoreText.Format("Resize_MinimumSize", ByteSize.Format(PartitionPlanner.MinimumSizeBytes)));
            return;
        }

        if (request.NewSizeBytes < floor)
        {
            error(CoreText.Format("Resize_BelowData", ByteSize.Format(volume.UsedBytes), ByteSize.Format(floor)));
            return;
        }

        if (limits is { } l && request.NewSizeBytes < l.MinimumBytes)
        {
            error(CoreText.Format("Resize_UnmovableFiles", ByteSize.Format(l.MinimumBytes)));
            return;
        }

        var freeAfter = request.NewSizeBytes - volume.UsedBytes;
        if (freeAfter < (ulong)(request.NewSizeBytes * ComfortableHeadroom))
        {
            warn(CoreText.Format("Resize_LittleFreeLeft", ByteSize.Format(freeAfter)));
        }
    }

    private static void ValidateExtend(Disk disk, Partition partition, ResizePartitionRequest request, SupportedSize? limits, Action<string> error)
    {
        var max = MaximumSize(disk, partition);
        if (max == partition.SizeBytes)
        {
            error(CoreText.Get("Resize_NoSpaceAfter"));
            return;
        }

        if (request.NewSizeBytes > max)
        {
            error(CoreText.Format("Resize_NotEnoughAfter", ByteSize.Format(max - partition.SizeBytes), ByteSize.Format(request.NewSizeBytes - partition.SizeBytes)));
            return;
        }

        if (limits is { } l && request.NewSizeBytes > l.MaximumBytes)
        {
            error(CoreText.Format("Resize_WindowsMaximum", ByteSize.Format(l.MaximumBytes)));
        }
    }
}
