using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>
/// Validation for the combined "shrink and create in one step" operation. The resize half is
/// checked against the live layout and the create half against the layout as it will be once the
/// shrink has happened, so both can be confirmed up front without an intermediate write.
/// </summary>
public static class ShrinkAndCreatePlanner
{
    public const ulong Alignment = FreeSpaceCalculator.DefaultAlignment;

    /// <summary>Aligns the new size down to 1 MiB, exactly as a plain shrink would.</summary>
    public static ShrinkAndCreateRequest Normalize(ShrinkAndCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var aligned = request.NewSizeBytes - (request.NewSizeBytes % Alignment);
        return aligned == request.NewSizeBytes ? request : request with { NewSizeBytes = aligned };
    }

    /// <summary>
    /// The create request for the space the shrink frees, or null when the request does not shrink
    /// the partition or the freed space is too small for a partition.
    /// </summary>
    public static CreatePartitionRequest? CreateRequest(ShrinkAndCreateRequest request, Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);

        if (request.NewSizeBytes >= partition.SizeBytes)
        {
            return null;
        }

        var offset = FreeSpaceCalculator.AlignUp(partition.OffsetBytes + request.NewSizeBytes, Alignment);
        var size = partition.EndBytes - offset;
        if (size < PartitionPlanner.MinimumSizeBytes)
        {
            return null;
        }

        return PartitionPlanner.Normalize(new CreatePartitionRequest
        {
            DiskNumber = disk.Number,
            OffsetBytes = offset,
            SizeBytes = size,
            FileSystem = request.FileSystem,
            Label = request.Label,
            DriveLetter = request.DriveLetter,
            QuickFormat = request.QuickFormat,
            ExpectedDisk = request.ExpectedDisk,
        });
    }

    public static IReadOnlyList<ValidationIssue> Validate(
        Disk disk,
        Partition partition,
        ShrinkAndCreateRequest request,
        SupportedSize? windowsLimits = null,
        IReadOnlySet<char>? lettersInUse = null)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));

        if (request.DiskNumber != disk.Number || request.PartitionNumber != partition.Number)
        {
            Error("The request does not match the given disk and partition.");
            return issues;
        }

        if (request.NewSizeBytes >= partition.SizeBytes)
        {
            Error("Shrink and create must shrink the partition; choose a size smaller than the current one.");
            return issues;
        }

        // Resize half, against the live layout.
        issues.AddRange(ResizePlanner.Validate(disk, partition, new ResizePartitionRequest
        {
            DiskNumber = request.DiskNumber,
            PartitionNumber = request.PartitionNumber,
            NewSizeBytes = request.NewSizeBytes,
            ExpectedDisk = request.ExpectedDisk,
            ExpectedPartition = request.ExpectedPartition,
        }, windowsLimits));

        // Create half, against the layout as it will be once the shrink has happened.
        var create = CreateRequest(request, disk, partition);
        if (create is null)
        {
            Error("The shrink does not free enough contiguous space for a new partition.");
        }
        else
        {
            var afterShrink = disk with
            {
                Partitions = disk.Partitions
                    .Select(p => p.Number == partition.Number ? partition with { SizeBytes = request.NewSizeBytes } : p)
                    .ToList(),
                LargestFreeExtentBytes = Math.Max(disk.LargestFreeExtentBytes, create.SizeBytes),
            };

            issues.AddRange(PartitionPlanner.Validate(afterShrink, create, lettersInUse));
        }

        return issues;
    }
}
