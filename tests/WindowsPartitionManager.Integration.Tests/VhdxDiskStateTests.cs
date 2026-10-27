using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>Offline and read-only on a real disk: both are reversible and the data survives.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxDiskStateTests(ITestOutputHelper output)
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    [AdminFact]
    public async Task ReadOnly_BlocksWrites_Offline_HidesTheDrive_BothComeBack()
    {
        using var vhd = TestVhd.Create(1 * GiB, output, "state");
        var ops = vhd.Operations;
        var n = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(n, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);
        var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
        var created = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = region.OffsetBytes, SizeBytes = region.SizeBytes, FileSystem = "NTFS", Label = "State" });
        var letter = created.DriveLetter!.Value;
        await File.WriteAllTextAsync($@"{letter}:\keep.txt", "stays");

        // Read-only: Windows refuses writes, the planner then refuses partition changes too.
        disk = await vhd.WaitAsync(d => d.Partitions.Any(p => p.Number == created.Number));
        var readOnly = await ops.SetDiskStateAsync(new SetDiskStateRequest { DiskNumber = n, Change = DiskStateChange.ReadOnly, ExpectedDisk = DiskFingerprint.Of(disk) });
        Assert.True(readOnly.IsReadOnly);
        await Task.Delay(2000); // the volume is remounted read-only
        Assert.ThrowsAny<IOException>(() => File.WriteAllText($@"{letter}:\new.txt", "refused"));
        Assert.Equal("stays", await File.ReadAllTextAsync($@"{letter}:\keep.txt"));

        var writable = await ops.SetDiskStateAsync(new SetDiskStateRequest { DiskNumber = n, Change = DiskStateChange.Writable });
        Assert.False(writable.IsReadOnly);

        // Offline: the drive disappears; online: it comes back with its files.
        var offline = await ops.SetDiskStateAsync(new SetDiskStateRequest { DiskNumber = n, Change = DiskStateChange.Offline });
        Assert.True(offline.IsOffline);
        Assert.False(Directory.Exists($@"{letter}:\"));

        var again = await Assert.ThrowsAsync<StorageOperationException>(() => ops.SetDiskStateAsync(new SetDiskStateRequest { DiskNumber = n, Change = DiskStateChange.Offline }));
        Assert.Contains("Refused before touching the disk", again.Message, StringComparison.Ordinal);

        var online = await ops.SetDiskStateAsync(new SetDiskStateRequest { DiskNumber = n, Change = DiskStateChange.Online });
        Assert.False(online.IsOffline);
        // Test VHDXs are attached without automatic drive letters, so Windows does not hand the
        // letter back by itself here (a real disk keeps it in the mount manager's database).
        await vhd.WaitAsync(d => d.Partitions.Any(p => p.Number == created.Number && p.Volume is not null));
        await vhd.EnsureDriveLetterAsync(created.Number);
        disk = await vhd.WaitAsync(d => d.Partitions.Any(p => p.Number == created.Number && p.DriveLetter is not null));
        var back = disk.Partitions.Single(p => p.Number == created.Number).DriveLetter!.Value;
        output.WriteLine($"Letter before offline {letter}:, after online {back}:");
        Assert.Equal("stays", await File.ReadAllTextAsync($@"{back}:\keep.txt"));
        await File.WriteAllTextAsync($@"{back}:\new.txt", "allowed again");
        Assert.Equal(0, vhd.Chkdsk(back));
    }
}
