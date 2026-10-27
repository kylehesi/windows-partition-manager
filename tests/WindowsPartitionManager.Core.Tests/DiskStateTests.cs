using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class DiskStateTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static Disk Disk(bool system = false, bool offline = false, bool readOnly = false, char? letter = 'E') => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = "USB",
        SizeBytes = 64 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 512,
        Style = PartitionStyle.Gpt,
        IsSystem = system,
        IsBoot = system,
        IsOffline = offline,
        IsReadOnly = readOnly,
        Partitions =
        [
            new Partition { Number = 1, OffsetBytes = 1024 * 1024, SizeBytes = 32 * GiB, Kind = PartitionKind.Basic, DriveLetter = letter },
        ],
    };

    private static SetDiskStateRequest Request(DiskStateChange change) => new() { DiskNumber = 1, Change = change };

    [Fact]
    public void WindowsDisk_OffersNothing_AndRefusesEverything()
    {
        var disk = Disk(system: true);

        Assert.Empty(DiskStatePlanner.Available(disk));
        foreach (var change in Enum.GetValues<DiskStateChange>())
        {
            Assert.Contains(DiskStatePlanner.Validate(disk, Request(change)), i => i.IsError);
        }
    }

    [Fact]
    public void Available_OffersTheOppositeOfTheCurrentState()
    {
        Assert.Equal([DiskStateChange.Offline, DiskStateChange.ReadOnly], DiskStatePlanner.Available(Disk()));
        Assert.Equal([DiskStateChange.Online, DiskStateChange.Writable], DiskStatePlanner.Available(Disk(offline: true, readOnly: true)));
    }

    [Fact]
    public void Offline_WarnsAboutTheDrivesThatDisappear()
    {
        var issues = DiskStatePlanner.Validate(Disk(), Request(DiskStateChange.Offline));

        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Message.Contains("E:", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadOnly_Warns_AndBringingBackIsSilent()
    {
        Assert.Contains(DiskStatePlanner.Validate(Disk(), Request(DiskStateChange.ReadOnly)), i => i.Severity == IssueSeverity.Warning);
        Assert.Empty(DiskStatePlanner.Validate(Disk(readOnly: true), Request(DiskStateChange.Writable)));
        Assert.Empty(DiskStatePlanner.Validate(Disk(offline: true), Request(DiskStateChange.Online)));
    }

    [Theory]
    [InlineData(DiskStateChange.Online, false, false)]
    [InlineData(DiskStateChange.Offline, true, false)]
    [InlineData(DiskStateChange.ReadOnly, false, true)]
    [InlineData(DiskStateChange.Writable, false, false)]
    public void ChangeToTheCurrentState_IsRefused(DiskStateChange change, bool offline, bool readOnly)
    {
        Assert.Contains(DiskStatePlanner.Validate(Disk(offline: offline, readOnly: readOnly), Request(change)), i => i.IsError);
    }
}
