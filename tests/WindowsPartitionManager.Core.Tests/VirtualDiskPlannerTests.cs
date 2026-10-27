using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class VirtualDiskPlannerTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static CreateVirtualDiskRequest Create(string path = @"C:\VMs\practice.vhdx", ulong size = 4 * GiB) => new() { Path = path, SizeBytes = size };

    private static Disk Virtual(string? path = @"C:\VMs\practice.vhdx", bool boot = false) => new()
    {
        Number = 3,
        FriendlyName = "Msft Virtual Disk",
        BusType = "File-backed virtual",
        SizeBytes = 4 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        IsBoot = boot,
        VirtualDiskPath = path,
        Partitions = [new Partition { Number = 2, OffsetBytes = 16 * MiB, SizeBytes = 1 * GiB, Kind = PartitionKind.Basic, DriveLetter = 'V' }],
    };

    [Fact]
    public void Create_InAFreeSpot_IsAllowed()
    {
        Assert.Empty(VirtualDiskPlanner.ValidateCreate(Create(), fileExists: false, freeBytesOnTarget: 100 * GiB));
    }

    [Theory]
    [InlineData(@"practice.vhdx")]
    [InlineData(@"C:\VMs\practice.vhd")]
    [InlineData(@"C:\VMs\practice.txt")]
    public void Create_NeedsAFullVhdxPath(string path)
    {
        Assert.Contains(VirtualDiskPlanner.ValidateCreate(Create(path), fileExists: false), i => i.IsError);
    }

    [Fact]
    public void Create_NeverOverwritesAFile()
    {
        Assert.Contains(VirtualDiskPlanner.ValidateCreate(Create(), fileExists: true), i => i.IsError);
    }

    [Fact]
    public void Create_RefusesSizesOutsideTheFormat_AndWarnsWhenTheDriveIsTooSmall()
    {
        Assert.Contains(VirtualDiskPlanner.ValidateCreate(Create(size: 10 * MiB), fileExists: false), i => i.IsError);
        Assert.Contains(VirtualDiskPlanner.ValidateCreate(Create(size: 65UL * 1024 * GiB), fileExists: false), i => i.IsError);

        var issues = VirtualDiskPlanner.ValidateCreate(Create(size: 50 * GiB), fileExists: false, freeBytesOnTarget: 10 * GiB);
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Detach_OnlyForVirtualDisks_AndWarnsAboutItsDrives()
    {
        var request = new DetachVirtualDiskRequest { DiskNumber = 3 };

        var issues = VirtualDiskPlanner.ValidateDetach(Virtual(), request);
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Message.Contains("V:", StringComparison.Ordinal));

        Assert.Contains(VirtualDiskPlanner.ValidateDetach(Virtual(path: null), request), i => i.IsError);
        Assert.Contains(VirtualDiskPlanner.ValidateDetach(Virtual(boot: true), request), i => i.IsError);
    }
}
