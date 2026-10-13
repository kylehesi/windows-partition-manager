namespace WindowsPartitionManager.Core.Operations;

/// <summary>
/// One combined operation: shrink a partition to <see cref="NewSizeBytes"/> and immediately turn
/// the space that frees up into a new, formatted partition. Validated by
/// <see cref="ShrinkAndCreatePlanner"/>; executed as a shrink followed by a create.
/// </summary>
public sealed record ShrinkAndCreateRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    /// <summary>Target size of the source partition after it shrinks (aligned by <see cref="ShrinkAndCreatePlanner.Normalize"/>).</summary>
    public required ulong NewSizeBytes { get; init; }

    /// <summary>"NTFS", "exFAT", "FAT32" or "ReFS" for the new partition.</summary>
    public string FileSystem { get; init; } = "NTFS";

    public string Label { get; init; } = string.Empty;

    /// <summary>Drive letter for the new partition, or null to let Windows pick the next free one.</summary>
    public char? DriveLetter { get; init; }

    /// <summary>Quick format (does not zero every sector). Full formats take hours on large disks.</summary>
    public bool QuickFormat { get; init; } = true;

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}
