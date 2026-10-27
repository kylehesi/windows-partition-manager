using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class CheckPlannerTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static (Disk Disk, Partition Partition) Layout(string fs = "NTFS", bool boot = false, bool readOnly = false)
    {
        var partition = new Partition
        {
            Number = 2,
            OffsetBytes = 1024 * 1024,
            SizeBytes = 10 * GiB,
            Kind = PartitionKind.Basic,
            DriveLetter = boot ? 'C' : 'E',
            IsBoot = boot,
            Volume = new Volume { Path = "v", FileSystem = fs, SizeBytes = 10 * GiB, FreeBytes = 5 * GiB },
        };
        var disk = new Disk
        {
            Number = 1,
            FriendlyName = "Test",
            BusType = "SATA",
            SizeBytes = 100 * GiB,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 4096,
            Style = PartitionStyle.Gpt,
            IsReadOnly = readOnly,
            Partitions = [partition],
        };
        return (disk, partition);
    }

    private static CheckVolumeRequest Request(CheckMode mode) => new() { DiskNumber = 1, PartitionNumber = 2, Mode = mode };

    [Fact]
    public void Ntfs_OffersAllThree_WithWarningsOnlyForFixes()
    {
        var (disk, partition) = Layout();

        Assert.Equal([CheckMode.Scan, CheckMode.SpotFix, CheckMode.OfflineScanAndFix], CheckPlanner.Available(disk, partition));
        Assert.Empty(CheckPlanner.Validate(disk, partition, Request(CheckMode.Scan)));
        Assert.Contains(CheckPlanner.Validate(disk, partition, Request(CheckMode.SpotFix)), i => i.Severity == IssueSeverity.Warning);
        Assert.Contains(CheckPlanner.Validate(disk, partition, Request(CheckMode.OfflineScanAndFix)), i => i.Message.Contains("E:", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsVolume_CanOnlyBeScanned_AndTheFixPointsToChkdsk()
    {
        var (disk, partition) = Layout(boot: true);

        Assert.Equal([CheckMode.Scan], CheckPlanner.Available(disk, partition));
        Assert.Contains(CheckPlanner.Validate(disk, partition, Request(CheckMode.OfflineScanAndFix)), i => i.IsError && i.Message.Contains("chkdsk C: /f", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("exFAT")]
    public void FatFamily_GetsTheFullCheckOnly(string fs)
    {
        var (disk, partition) = Layout(fs);

        Assert.Equal([CheckMode.OfflineScanAndFix], CheckPlanner.Available(disk, partition));
        Assert.Contains(CheckPlanner.Validate(disk, partition, Request(CheckMode.Scan)), i => i.IsError);
    }

    [Fact]
    public void ReadOnlyDisk_CanBeScannedButNotFixed()
    {
        var (disk, partition) = Layout(readOnly: true);

        Assert.Empty(CheckPlanner.Validate(disk, partition, Request(CheckMode.Scan)));
        Assert.Contains(CheckPlanner.Validate(disk, partition, Request(CheckMode.SpotFix)), i => i.IsError);
    }

    [Fact]
    public void ReFS_IsNotOffered()
    {
        var (disk, partition) = Layout("ReFS");
        Assert.Empty(CheckPlanner.Available(disk, partition));
    }

    [Theory]
    [InlineData(0u, CheckOutcome.Clean)]
    [InlineData(4u, CheckOutcome.Clean)]
    [InlineData(1u, CheckOutcome.Fixed)]
    [InlineData(7u, CheckOutcome.NeedsSpotFix)]
    [InlineData(13u, CheckOutcome.NeedsOfflineFix)]
    [InlineData(10u, CheckOutcome.Retry)]
    [InlineData(3u, CheckOutcome.Failed)]
    [InlineData(999u, CheckOutcome.Failed)]
    public void StatusCodes_MapToOutcomes(uint code, CheckOutcome outcome)
    {
        Assert.Equal(outcome, CheckResult.FromCode(code).Outcome);
    }
}
