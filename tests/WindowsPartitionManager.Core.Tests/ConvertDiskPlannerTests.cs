using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class ConvertDiskPlannerTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static readonly Partition Msr = new() { Number = 1, OffsetBytes = 1 * MiB, SizeBytes = 16 * MiB, Kind = PartitionKind.MicrosoftReserved };

    private static readonly Partition Data = new() { Number = 2, OffsetBytes = 17 * MiB, SizeBytes = 1 * GiB, Kind = PartitionKind.Basic };

    private static Disk Disk(PartitionStyle style, ulong size = 64 * GiB, bool system = false, params Partition[] partitions) => new()
    {
        Number = 2,
        FriendlyName = "Test",
        BusType = "SATA",
        SizeBytes = size,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 512,
        Style = style,
        IsSystem = system,
        IsBoot = system,
        Partitions = partitions,
    };

    private static ConvertDiskRequest To(PartitionStyle style) => new() { DiskNumber = 2, Style = style };

    [Fact]
    public void EmptyGptDisk_WithOnlyTheReservedPartition_CanBecomeMbr()
    {
        var disk = Disk(PartitionStyle.Gpt, partitions: Msr);

        Assert.Equal(PartitionStyle.Mbr, ConvertDiskPlanner.Target(disk));
        Assert.Empty(ConvertDiskPlanner.Validate(disk, To(PartitionStyle.Mbr)));
    }

    [Fact]
    public void EmptyMbrDisk_CanBecomeGpt()
    {
        var disk = Disk(PartitionStyle.Mbr);
        Assert.Equal(PartitionStyle.Gpt, ConvertDiskPlanner.Target(disk));
        Assert.Empty(ConvertDiskPlanner.Validate(disk, To(PartitionStyle.Gpt)));
    }

    [Fact]
    public void AnyDataPartition_BlocksTheConversion()
    {
        var disk = Disk(PartitionStyle.Gpt, partitions: [Msr, Data]);

        Assert.Null(ConvertDiskPlanner.Target(disk));
        Assert.Contains(ConvertDiskPlanner.Validate(disk, To(PartitionStyle.Mbr)), i => i.IsError && i.Message.Contains('1', StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsDisk_RawDisk_AndTheSameStyle_AreRefused()
    {
        Assert.Contains(ConvertDiskPlanner.Validate(Disk(PartitionStyle.Gpt, system: true), To(PartitionStyle.Mbr)), i => i.IsError && i.Message.Contains("MBR2GPT", StringComparison.Ordinal));
        Assert.Contains(ConvertDiskPlanner.Validate(Disk(PartitionStyle.Raw), To(PartitionStyle.Gpt)), i => i.IsError);
        Assert.Contains(ConvertDiskPlanner.Validate(Disk(PartitionStyle.Gpt), To(PartitionStyle.Gpt)), i => i.IsError);
        Assert.Null(ConvertDiskPlanner.Target(Disk(PartitionStyle.Raw)));
    }

    [Fact]
    public void MbrOnALargeDisk_WarnsAboutTheTwoTerabyteLimit()
    {
        var issues = ConvertDiskPlanner.Validate(Disk(PartitionStyle.Gpt, size: 4096 * GiB), To(PartitionStyle.Mbr));

        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("2 TB", StringComparison.Ordinal));
    }
}
