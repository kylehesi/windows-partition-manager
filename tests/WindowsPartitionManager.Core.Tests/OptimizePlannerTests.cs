using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class OptimizePlannerTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static (Disk Disk, Partition Partition) Layout(string fs = "NTFS", bool readOnly = false)
    {
        var partition = new Partition
        {
            Number = 2,
            OffsetBytes = 1024 * 1024,
            SizeBytes = 10 * GiB,
            Kind = PartitionKind.Basic,
            DriveLetter = 'C',
            IsBoot = true,
            Volume = new Volume { Path = "v", FileSystem = fs, SizeBytes = 10 * GiB, FreeBytes = 5 * GiB },
        };
        return (new Disk
        {
            Number = 0,
            FriendlyName = "Test",
            BusType = "NVMe",
            SizeBytes = 100 * GiB,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 4096,
            Style = PartitionStyle.Gpt,
            IsReadOnly = readOnly,
            Partitions = [partition],
        }, partition);
    }

    private static OptimizeVolumeRequest Request(OptimizeMode mode) => new() { DiskNumber = 0, PartitionNumber = 2, Mode = mode };

    [Fact]
    public void Ntfs_RecommendsTrimOnSsd_AndDefragOnHdd()
    {
        var (disk, partition) = Layout();

        Assert.Equal(OptimizeMode.ReTrim, OptimizePlanner.Available(disk, partition, MediaType.Ssd)[0]);
        Assert.Equal(OptimizeMode.Defrag, OptimizePlanner.Available(disk, partition, MediaType.Hdd)[0]);
    }

    [Fact]
    public void WindowsVolume_CanBeOptimized_TrimNeedsNoWarningOnSsd()
    {
        var (disk, partition) = Layout();
        Assert.Empty(OptimizePlanner.Validate(disk, partition, Request(OptimizeMode.ReTrim), MediaType.Ssd));
    }

    [Fact]
    public void DefragOnSsd_And_TrimOnHdd_AreAllowedButExplained()
    {
        var (disk, partition) = Layout();

        Assert.Contains(OptimizePlanner.Validate(disk, partition, Request(OptimizeMode.Defrag), MediaType.Ssd), i => i.Severity == IssueSeverity.Warning && i.Message.Contains("wear", StringComparison.Ordinal));
        Assert.Contains(OptimizePlanner.Validate(disk, partition, Request(OptimizeMode.ReTrim), MediaType.Hdd), i => i.Severity == IssueSeverity.Warning);
    }

    [Theory]
    [InlineData("ReFS", new[] { OptimizeMode.ReTrim })]
    [InlineData("FAT32", new[] { OptimizeMode.Defrag })]
    [InlineData("exFAT", new OptimizeMode[0])]
    public void FileSystems_GetWhatWindowsSupports(string fs, OptimizeMode[] expected)
    {
        var (disk, partition) = Layout(fs);
        Assert.Equal(expected, OptimizePlanner.Available(disk, partition));
    }

    [Fact]
    public void ReadOnlyDisk_IsRefused()
    {
        var (disk, partition) = Layout(readOnly: true);

        Assert.Empty(OptimizePlanner.Available(disk, partition));
        Assert.Contains(OptimizePlanner.Validate(disk, partition, Request(OptimizeMode.ReTrim)), i => i.IsError);
    }
}
