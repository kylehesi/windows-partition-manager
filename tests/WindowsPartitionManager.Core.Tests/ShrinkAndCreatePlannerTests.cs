using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public sealed class ShrinkAndCreatePlannerTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Disk GptDisk(params Partition[] partitions) => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = "SATA",
        SizeBytes = 100 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        Partitions = partitions,
    };

    private static Partition Ntfs(int number, ulong offset, ulong size, ulong used, string fs = "NTFS") => new()
    {
        Number = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Kind = PartitionKind.Basic,
        TypeDescription = "Basic data",
        DriveLetter = 'D',
        Volume = new Volume { Path = "v", FileSystem = fs, SizeBytes = size, FreeBytes = size - used, DriveLetter = 'D' },
    };

    private static ShrinkAndCreateRequest Request(ulong newSize) => new()
    {
        DiskNumber = 1,
        PartitionNumber = 1,
        NewSizeBytes = newSize,
        FileSystem = "NTFS",
        Label = "Freed",
        DriveLetter = 'E',
    };

    [Fact]
    public void ShrinkAndCreate_WithinLimits_IsAllowed()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);
        var disk = GptDisk(partition);

        var issues = ShrinkAndCreatePlanner.Validate(disk, partition, Request(20 * GiB), new SupportedSize(15 * GiB, 99 * GiB));

        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void CreateRequest_ComputesTheFreedRegion()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);
        var disk = GptDisk(partition);

        var create = ShrinkAndCreatePlanner.CreateRequest(Request(20 * GiB), disk, partition);

        Assert.NotNull(create);
        Assert.Equal(20 * GiB + 1 * MiB, create!.OffsetBytes);
        Assert.Equal(30 * GiB, create.SizeBytes);
        Assert.Equal("NTFS", create.FileSystem);
        Assert.Equal("Freed", create.Label);
        Assert.Equal('E', create.DriveLetter);
    }

    [Fact]
    public void NotAShrink_IsRejected()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);

        var issues = ShrinkAndCreatePlanner.Validate(GptDisk(partition), partition, Request(50 * GiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("smaller than the current one", StringComparison.Ordinal));
        Assert.Null(ShrinkAndCreatePlanner.CreateRequest(Request(50 * GiB), GptDisk(partition), partition));
    }

    [Fact]
    public void ShrinkBelowUsedData_IsRejectedByTheResizeHalf()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 30 * GiB);

        var issues = ShrinkAndCreatePlanner.Validate(GptDisk(partition), partition, Request(20 * GiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("cannot shrink below", StringComparison.Ordinal));
    }

    [Fact]
    public void FreedSpaceTooSmall_IsRejected()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);

        var issues = ShrinkAndCreatePlanner.Validate(GptDisk(partition), partition, Request(50 * GiB - 4 * MiB));

        Assert.Contains(issues, i => i.IsError && i.Message.Contains("does not free enough", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateRequest_UsesNextFreeDriveLetter_WhenGiven()
    {
        var partition = Ntfs(1, 1 * MiB, 50 * GiB, used: 10 * GiB);
        var disk = GptDisk(partition);

        // The source already uses D:, so E: is free and must be accepted.
        var issues = ShrinkAndCreatePlanner.Validate(disk, partition, Request(20 * GiB), null, new HashSet<char> { 'D' });

        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void Normalize_AlignsDownToMegabyte()
    {
        var normalized = ShrinkAndCreatePlanner.Normalize(Request(20 * GiB + 700 * 1024));

        Assert.Equal(20 * GiB, normalized.NewSizeBytes);
    }
}
