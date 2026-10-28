using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>
/// A change waiting in the queue. Existing partitions are addressed by where they start on the
/// disk, not by their number: Windows renumbers partitions when others are created or deleted,
/// but an earlier step never moves the start of a partition a later step refers to.
/// </summary>
public abstract record PlannedOperation(int DiskNumber)
{
    /// <summary>One line for the queue list, in the UI language.</summary>
    public abstract string Describe(Disk projectedBefore);

    protected static string NameOf(Disk disk, ulong offset)
        => disk.Partitions.FirstOrDefault(p => p.OffsetBytes == offset) is { } p
            ? p.DriveLetter is { } l ? $"{l}:" : CoreText.Format("Queue_PartitionAt", p.Number)
            : CoreText.Format("Queue_PartitionAtOffset", ByteSize.Format(offset));
}

public sealed record PlannedCreate(
    int DiskNumber,
    ulong OffsetBytes,
    ulong SizeBytes,
    string FileSystem,
    string Label,
    char? DriveLetter,
    bool QuickFormat = true) : PlannedOperation(DiskNumber)
{
    public override string Describe(Disk projectedBefore)
        => CoreText.Format("Queue_Create", ByteSize.Format(SizeBytes), FileSystem, Label.Length > 0 ? $" \"{Label}\"" : string.Empty, DiskNumber, ByteSize.Format(OffsetBytes), DriveLetter is { } l ? $" {l}:" : string.Empty);

    public CreatePartitionRequest ToRequest() => new()
    {
        DiskNumber = DiskNumber,
        OffsetBytes = OffsetBytes,
        SizeBytes = SizeBytes,
        FileSystem = FileSystem,
        Label = Label,
        DriveLetter = DriveLetter,
        QuickFormat = QuickFormat,
    };
}

public sealed record PlannedDelete(int DiskNumber, ulong OffsetBytes) : PlannedOperation(DiskNumber)
{
    public override string Describe(Disk projectedBefore)
        => CoreText.Format("Queue_Delete", NameOf(projectedBefore, OffsetBytes), DiskNumber);
}

public sealed record PlannedResize(int DiskNumber, ulong OffsetBytes, ulong NewSizeBytes) : PlannedOperation(DiskNumber)
{
    public override string Describe(Disk projectedBefore)
    {
        var current = projectedBefore.Partitions.FirstOrDefault(p => p.OffsetBytes == OffsetBytes)?.SizeBytes ?? NewSizeBytes;
        return CoreText.Format(NewSizeBytes < current ? "Queue_Shrink" : "Queue_Extend", NameOf(projectedBefore, OffsetBytes), DiskNumber, ByteSize.Format(NewSizeBytes));
    }
}

public sealed record PlannedFormat(int DiskNumber, ulong OffsetBytes, string FileSystem, string Label, bool QuickFormat = true) : PlannedOperation(DiskNumber)
{
    public override string Describe(Disk projectedBefore)
        => CoreText.Format("Queue_Format", NameOf(projectedBefore, OffsetBytes), DiskNumber, FileSystem, Label.Length > 0 ? $" \"{Label}\"" : string.Empty);
}

/// <summary>
/// Applies a planned change to a disk on paper, so the next step can be checked against the
/// layout it will really meet and the UI can show the result before anything is written.
/// </summary>
public static class LayoutSimulator
{
    /// <summary>Volumes created on paper carry this path prefix, so the UI can mark them as planned.</summary>
    public const string PlannedVolumePrefix = "planned:";

    public static bool IsPlanned(Partition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return partition.Volume?.Path.StartsWith(PlannedVolumePrefix, StringComparison.Ordinal) == true;
    }

    public static Disk Apply(Disk disk, PlannedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(operation);

        var partitions = disk.Partitions.ToList();
        switch (operation)
        {
            case PlannedCreate create:
                partitions.Add(new Partition
                {
                    // Windows picks the number; the next free one is the usual result.
                    Number = partitions.Count == 0 ? 1 : partitions.Max(p => p.Number) + 1,
                    OffsetBytes = create.OffsetBytes,
                    SizeBytes = create.SizeBytes,
                    Kind = PartitionKind.Basic,
                    TypeDescription = "Basic data",
                    DriveLetter = create.DriveLetter,
                    Volume = new Volume
                    {
                        Path = PlannedVolumePrefix + create.OffsetBytes,
                        DriveLetter = create.DriveLetter,
                        Label = create.Label.Length > 0 ? create.Label : null,
                        FileSystem = create.FileSystem,
                        SizeBytes = create.SizeBytes,
                        FreeBytes = create.SizeBytes,
                    },
                });
                break;

            case PlannedDelete delete:
                partitions.RemoveAll(p => p.OffsetBytes == delete.OffsetBytes);
                break;

            case PlannedResize resize:
                Replace(partitions, resize.OffsetBytes, p => p with
                {
                    SizeBytes = resize.NewSizeBytes,
                    Volume = p.Volume is { } v
                        ? v with { SizeBytes = resize.NewSizeBytes, FreeBytes = resize.NewSizeBytes > v.UsedBytes ? resize.NewSizeBytes - v.UsedBytes : 0 }
                        : null,
                });
                break;

            case PlannedFormat format:
                Replace(partitions, format.OffsetBytes, p => p with
                {
                    Volume = p.Volume is { } v
                        ? v with
                        {
                            FileSystem = format.FileSystem,
                            Label = format.Label.Length > 0 ? format.Label : null,
                            FreeBytes = v.SizeBytes,
                            IsDirty = false,
                            Health = VolumeHealth.Healthy,
                            BitLocker = null,
                        }
                        : null,
                });
                break;

            default:
                throw new ArgumentException($"Unknown planned operation {operation.GetType().Name}.", nameof(operation));
        }

        var result = disk with { Partitions = [.. partitions.OrderBy(p => p.OffsetBytes)] };

        // Windows' own largest-free-extent figure is only known for the real disk; on paper our
        // own free-space calculation (never larger than Windows') stands in for it.
        return result with { LargestFreeExtentBytes = Layout.FreeSpaceCalculator.Compute(result).Select(r => r.SizeBytes).DefaultIfEmpty(0UL).Max() };
    }

    private static void Replace(List<Partition> partitions, ulong offset, Func<Partition, Partition> change)
    {
        var index = partitions.FindIndex(p => p.OffsetBytes == offset);
        if (index >= 0)
        {
            partitions[index] = change(partitions[index]);
        }
    }
}
