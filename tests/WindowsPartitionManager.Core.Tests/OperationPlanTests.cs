using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class OperationPlanTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Partition Data(int number, ulong offset, ulong size, char letter, ulong used) => new()
    {
        Number = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Kind = PartitionKind.Basic,
        TypeDescription = "Basic data",
        DriveLetter = letter,
        Volume = new Volume { Path = $"v{number}", FileSystem = "NTFS", SizeBytes = size, FreeBytes = size - used, DriveLetter = letter },
    };

    /// <summary>A 100 GiB data disk: E: fills the first 60 GiB, the rest is free.</summary>
    private static Disk Disk() => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = "SATA",
        SizeBytes = 100 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        Partitions = [Data(1, 1 * MiB, 60 * GiB, 'E', used: 10 * GiB)],
        LargestFreeExtentBytes = 40 * GiB - (2 * MiB),
    };

    private static OperationPlan Queue() => new([Disk()], new HashSet<char> { 'C' });

    [Fact]
    public void Steps_BuildOnEachOther_AndTheProjectionShowsTheResult()
    {
        var queue = Queue();
        const ulong eStart = 1 * MiB;

        // Shrink E: to 20 GiB, then create F: in the space it frees.
        Assert.DoesNotContain(queue.TryAdd(new PlannedResize(1, eStart, 20 * GiB)), i => i.IsError);
        var freed = FreeSpaceCalculator.Compute(queue.Projected[0])[0];
        Assert.Equal(eStart + (20 * GiB), freed.OffsetBytes);
        Assert.DoesNotContain(queue.TryAdd(new PlannedCreate(1, freed.OffsetBytes, 30 * GiB, "NTFS", "Games", 'F')), i => i.IsError);

        var projected = queue.Projected[0];
        Assert.Equal(2, queue.Steps.Count);
        Assert.Equal([20 * GiB, 30 * GiB], projected.Partitions.Select(p => p.SizeBytes));
        Assert.True(LayoutSimulator.IsPlanned(projected.Partitions[1]));
        Assert.Contains("F:", queue.Steps[1].Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepThatDoesNotFitThePlan_IsRefused_AndNothingIsAdded()
    {
        var queue = Queue();

        // Shrinking below the data on E: is refused like an immediate shrink would be.
        Assert.Contains(queue.TryAdd(new PlannedResize(1, 1 * MiB, 5 * GiB)), i => i.IsError);

        // A letter already planned for another partition is taken.
        var free = FreeSpaceCalculator.Compute(queue.Projected[0])[0];
        queue.TryAdd(new PlannedCreate(1, free.OffsetBytes, 10 * GiB, "NTFS", string.Empty, 'G'));
        Assert.Contains(queue.TryAdd(new PlannedCreate(1, free.OffsetBytes + (10 * GiB), 10 * GiB, "NTFS", string.Empty, 'G')), i => i.IsError);
        Assert.Single(queue.Steps);
    }

    [Fact]
    public void RemovingAStep_DropsTheStepsThatDependedOnIt()
    {
        var queue = Queue();
        var free = FreeSpaceCalculator.Compute(queue.Projected[0])[0];
        queue.TryAdd(new PlannedCreate(1, free.OffsetBytes, 10 * GiB, "NTFS", "New", 'H'));
        queue.TryAdd(new PlannedFormat(1, free.OffsetBytes, "exFAT", "Stick"));
        queue.TryAdd(new PlannedResize(1, 1 * MiB, 40 * GiB));

        var removed = queue.RemoveAt(0);

        // The format pointed at the partition the first step would have created.
        Assert.Equal(2, removed.Count);
        Assert.IsType<PlannedFormat>(removed[1].Operation);
        var remaining = Assert.Single(queue.Steps);
        Assert.IsType<PlannedResize>(remaining.Operation);
    }

    [Fact]
    public void DeletingThenCreating_ReusesTheSpace()
    {
        var queue = Queue();

        Assert.DoesNotContain(queue.TryAdd(new PlannedDelete(1, 1 * MiB)), i => i.IsError);
        var whole = FreeSpaceCalculator.Compute(queue.Projected[0]).Single();
        Assert.DoesNotContain(queue.TryAdd(new PlannedCreate(1, whole.OffsetBytes, whole.SizeBytes, "exFAT", "All", null)), i => i.IsError);
        Assert.Single(queue.Projected[0].Partitions);
    }

    [Fact]
    public void Rebase_DropsStepsThatNoLongerFitTheRealDisk()
    {
        var queue = Queue();
        queue.TryAdd(new PlannedFormat(1, 1 * MiB, "exFAT", "X"));

        // E: is gone from the real disk (deleted elsewhere in the meantime).
        var dropped = queue.Rebase([Disk() with { Partitions = [] }]);

        Assert.Single(dropped);
        Assert.True(queue.IsEmpty);
    }
}
