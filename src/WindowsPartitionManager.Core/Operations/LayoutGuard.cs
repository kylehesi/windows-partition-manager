using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>
/// What a disk looked like when the user chose an action. Disk numbers are not stable (a USB
/// drive unplugged and plugged back in can take another number), so the size, model and serial
/// number are compared too.
/// </summary>
public sealed record DiskFingerprint(int Number, ulong SizeBytes, string FriendlyName, string? SerialNumber)
{
    public static DiskFingerprint Of(Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return new DiskFingerprint(disk.Number, disk.SizeBytes, disk.FriendlyName, disk.SerialNumber);
    }

    public bool Matches(Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return disk.Number == Number
               && disk.SizeBytes == SizeBytes
               && string.Equals(disk.FriendlyName, FriendlyName, StringComparison.Ordinal)
               && string.Equals(disk.SerialNumber ?? string.Empty, SerialNumber ?? string.Empty, StringComparison.Ordinal);
    }
}

/// <summary>
/// What a partition looked like when the user chose an action. Partition numbers can be
/// reassigned when other partitions are deleted or created, so the offset and size are compared.
/// </summary>
public sealed record PartitionFingerprint(int Number, ulong OffsetBytes, ulong SizeBytes)
{
    public static PartitionFingerprint Of(Partition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return new PartitionFingerprint(partition.Number, partition.OffsetBytes, partition.SizeBytes);
    }

    public bool Matches(Partition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return partition.Number == Number && partition.OffsetBytes == OffsetBytes && partition.SizeBytes == SizeBytes;
    }
}

/// <summary>
/// The last check before a write: the disk and partition the user saw must still be exactly
/// the ones Windows would now act on. Anything else means the screen is out of date.
/// </summary>
public static class LayoutGuard
{
    public static string RefreshHint => CoreText.Get("Guard_RefreshHint");

    /// <summary>Returns an error issue when the fresh layout does not match what the user saw, otherwise null.</summary>
    public static ValidationIssue? Check(Disk? current, DiskFingerprint? expectedDisk, Partition? currentPartition = null, PartitionFingerprint? expectedPartition = null)
    {
        if (current is null)
        {
            return new ValidationIssue(IssueSeverity.Error, CoreText.Format("Guard_DiskGone", RefreshHint));
        }

        if (expectedDisk is not null && !expectedDisk.Matches(current))
        {
            return new ValidationIssue(IssueSeverity.Error, CoreText.Format("Guard_DiskChanged", current.Number, current.FriendlyName, ByteSize.Format(current.SizeBytes), expectedDisk.FriendlyName, ByteSize.Format(expectedDisk.SizeBytes), RefreshHint));
        }

        if (expectedPartition is not null)
        {
            if (currentPartition is null)
            {
                return new ValidationIssue(IssueSeverity.Error, CoreText.Format("Guard_PartitionGone", expectedPartition.Number, RefreshHint));
            }

            if (!expectedPartition.Matches(currentPartition))
            {
                return new ValidationIssue(IssueSeverity.Error, CoreText.Format("Guard_PartitionMoved", expectedPartition.Number, ByteSize.Format(currentPartition.OffsetBytes), ByteSize.Format(currentPartition.SizeBytes), ByteSize.Format(expectedPartition.OffsetBytes), ByteSize.Format(expectedPartition.SizeBytes), RefreshHint));
            }
        }

        return null;
    }
}

/// <summary>Delete one partition; the fingerprints make sure it is the one the user saw.</summary>
public sealed record DeletePartitionRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}
