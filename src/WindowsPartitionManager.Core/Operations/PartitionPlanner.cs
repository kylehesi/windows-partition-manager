using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>
/// Checks a <see cref="CreatePartitionRequest"/> against the current disk layout before anything
/// is written. Pure logic over the model so every rule is unit-testable.
/// </summary>
public static class PartitionPlanner
{
    public const ulong Alignment = FreeSpaceCalculator.DefaultAlignment;

    /// <summary>Smallest partition we agree to create; anything less is useless and some file systems refuse it.</summary>
    public const ulong MinimumSizeBytes = 8 * 1024 * 1024;

    /// <summary>Windows' own format tool refuses FAT32 volumes above 32 GB.</summary>
    public const ulong Fat32MaximumBytes = 32UL * 1024 * 1024 * 1024;

    private const int MbrPrimaryPartitionLimit = 4;

    public static readonly IReadOnlyList<string> SupportedFileSystems = ["NTFS", "exFAT", "FAT32", "ReFS"];

    /// <summary>
    /// Aligns the start up to 1 MiB and trims the size so the end does not move, mirroring what
    /// Disk Management does silently. Returns the request unchanged when it is already aligned.
    /// </summary>
    public static CreatePartitionRequest Normalize(CreatePartitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var alignedOffset = FreeSpaceCalculator.AlignUp(request.OffsetBytes, Alignment);
        if (alignedOffset == request.OffsetBytes)
        {
            return request;
        }

        var shift = alignedOffset - request.OffsetBytes;
        var size = request.SizeBytes > shift ? request.SizeBytes - shift : 0;
        return request with { OffsetBytes = alignedOffset, SizeBytes = size };
    }

    /// <summary>The free region of <paramref name="disk"/> that contains the request, or null.</summary>
    public static FreeRegion? FindContainingRegion(Disk disk, CreatePartitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(request);

        return FreeSpaceCalculator.Compute(disk)
            .FirstOrDefault(r => r.OffsetBytes <= request.OffsetBytes && request.EndBytes <= r.EndBytes);
    }

    /// <summary>Largest size that fits at the request's offset inside its free region, or 0.</summary>
    public static ulong MaximumSizeAt(Disk disk, ulong offsetBytes)
    {
        ArgumentNullException.ThrowIfNull(disk);

        var aligned = FreeSpaceCalculator.AlignUp(offsetBytes, Alignment);
        var region = FreeSpaceCalculator.Compute(disk).FirstOrDefault(r => r.OffsetBytes <= aligned && aligned < r.EndBytes);
        return region is null ? 0 : region.EndBytes - aligned;
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, CreatePartitionRequest request, IReadOnlySet<char>? lettersInUse = null)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

        if (request.DiskNumber != disk.Number)
        {
            Error(CoreText.Format("Create_WrongDisk", request.DiskNumber, disk.Number));
            return issues;
        }

        if (disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown)
        {
            Error(CoreText.Get("Create_NoPartitionTable"));
        }

        if (disk.IsReadOnly)
        {
            Error(CoreText.Get("Common_DiskReadOnly"));
        }

        if (disk.IsOffline)
        {
            Error(CoreText.Get("Create_DiskOfflineBringOnline"));
        }

        if (disk.Style == PartitionStyle.Mbr && disk.Partitions.Count(p => p.Kind != PartitionKind.Extended) >= MbrPrimaryPartitionLimit)
        {
            Error(CoreText.Get("Create_MbrPrimaryLimit"));
        }

        if (request.OffsetBytes % Alignment != 0)
        {
            Error(CoreText.Format("Create_OffsetNotAligned", ByteSize.Format(Alignment)));
        }

        if (request.SizeBytes < MinimumSizeBytes)
        {
            Error(CoreText.Format("Create_TooSmall", ByteSize.Format(MinimumSizeBytes)));
        }

        // Without a partition table there is no free region to look for.
        if (disk.Style is not (PartitionStyle.Raw or PartitionStyle.Unknown))
        {
            if (FindContainingRegion(disk, request) is null)
            {
                var max = MaximumSizeAt(disk, request.OffsetBytes);
                Error(max == 0
                    ? CoreText.Format("Create_OffsetNotFree", ByteSize.Format(request.OffsetBytes))
                    : CoreText.Format("Create_DoesNotFit", ByteSize.Format(max), ByteSize.Format(request.SizeBytes)));
            }
        }

        // Windows' own number is the final word: CreatePartition fails with "not enough free space" above it.
        if (disk.LargestFreeExtentBytes > 0 && request.SizeBytes > disk.LargestFreeExtentBytes)
        {
            Error(CoreText.Format("Create_LargestFreeExtent", ByteSize.Format(disk.LargestFreeExtentBytes), ByteSize.Format(request.SizeBytes)));
        }

        if (!SupportedFileSystems.Contains(request.FileSystem, StringComparer.OrdinalIgnoreCase))
        {
            Error(CoreText.Format("Common_UnsupportedFileSystem", request.FileSystem, string.Join(", ", SupportedFileSystems)));
        }
        else if (request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && request.SizeBytes > Fat32MaximumBytes)
        {
            Error(CoreText.Get("Common_Fat32TooLarge"));
        }

        var labelLimit = request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? 11 : 32;
        if (request.Label.Length > labelLimit)
        {
            Error(CoreText.Format("Common_LabelTooLong", request.FileSystem, labelLimit));
        }

        if (request.DriveLetter is { } letter)
        {
            var upper = char.ToUpperInvariant(letter);
            if (upper < 'C' || upper > 'Z')
            {
                Error(CoreText.Get("Create_LetterRange"));
            }
            else if (lettersInUse is not null && lettersInUse.Contains(upper))
            {
                Error(CoreText.Format("Create_LetterInUse", upper));
            }
        }
        else
        {
            Warn(CoreText.Get("Create_NoLetter"));
        }

        if (!request.QuickFormat)
        {
            Warn(CoreText.Get("Common_FullFormatSlow"));
        }

        issues.AddRange(SafetyChecks.Disk(disk));
        return issues;
    }

    /// <summary>
    /// Rules for deleting a partition. Partitions Windows needs to boot are refused outright;
    /// everything else is allowed with a data-loss warning, since deletion is the only way to
    /// repartition a FAT32 or exFAT drive that cannot be shrunk.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateDelete(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

        if (disk.IsReadOnly)
        {
            Error(CoreText.Get("Common_DiskReadOnly"));
        }

        if (disk.IsOffline)
        {
            Error(CoreText.Get("Common_DiskOffline"));
        }

        if (partition.IsBoot || partition.IsSystem)
        {
            Error(CoreText.Get("Delete_WindowsPartition"));
        }
        else if ((disk.IsSystem || disk.IsBoot) && partition.Kind != PartitionKind.Basic)
        {
            Error(CoreText.Format("Delete_NeededToStart", CoreText.PartitionType(partition.TypeDescription)));
        }

        if (partition.Kind == PartitionKind.Extended)
        {
            Error(CoreText.Get("Delete_LogicalDrivesFirst"));
        }

        if (partition.Volume is { } encrypted)
        {
            issues.AddRange(SafetyChecks.Encryption(encrypted, EncryptionUse.Destroy));
        }

        if (partition.Volume is { } volume && volume.UsedBytes > 0)
        {
            Warn(CoreText.Format("Common_VolumeHoldsData", ByteSize.Format(volume.UsedBytes), volume.Label is { Length: > 0 } l ? $" (\"{l}\")" : string.Empty));
        }
        else
        {
            Warn(CoreText.Get("Common_EverythingLost"));
        }

        issues.AddRange(SafetyChecks.Disk(disk));
        return issues;
    }

    /// <summary>Drive letters currently assigned to any partition or volume on the given disks.</summary>
    public static IReadOnlySet<char> LettersInUse(IEnumerable<Disk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);

        var letters = new HashSet<char>();
        foreach (var disk in disks)
        {
            foreach (var partition in disk.Partitions)
            {
                if (partition.DriveLetter is { } l)
                {
                    letters.Add(char.ToUpperInvariant(l));
                }

                if (partition.Volume?.DriveLetter is { } v)
                {
                    letters.Add(char.ToUpperInvariant(v));
                }
            }
        }

        return letters;
    }
}
