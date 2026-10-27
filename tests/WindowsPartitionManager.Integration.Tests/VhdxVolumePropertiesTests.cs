using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>Drive letter and label changes on a real volume: the data stays, only the name changes.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxVolumePropertiesTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task ChangeLetter_Rename_RemoveLetter_AndGiveItBack_KeepTheFiles()
    {
        using var vhd = TestVhd.Create(1 * GiB, output, "letter");
        var ops = vhd.Operations;
        var n = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(n, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);
        var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
        var created = await ops.CreatePartitionAsync(new CreatePartitionRequest
        {
            DiskNumber = n,
            OffsetBytes = region.OffsetBytes,
            SizeBytes = region.SizeBytes,
            FileSystem = "NTFS",
            Label = "Before",
        });
        var oldLetter = created.DriveLetter!.Value;
        await File.WriteAllTextAsync($@"{oldLetter}:\keep.txt", "stays");

        // A free letter, picked from the end of the alphabet like Disk Management does.
        var used = new HashSet<char>(Environment.GetLogicalDrives().Where(d => d.Length > 0).Select(d => char.ToUpperInvariant(d[0])));
        var newLetter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Reverse().First(c => !used.Contains(c));

        disk = await vhd.WaitAsync(d => d.Partitions.Any(p => p.Number == created.Number && p.Volume is not null));
        var partition = disk.Partitions.Single(p => p.Number == created.Number);
        Assert.True(DriveLetterPlanner.CanChange(disk, partition));

        var moved = await ops.ChangeDriveLetterAsync(new ChangeDriveLetterRequest
        {
            DiskNumber = n,
            PartitionNumber = partition.Number,
            NewDriveLetter = newLetter,
            ExpectedDisk = DiskFingerprint.Of(disk),
            ExpectedPartition = PartitionFingerprint.Of(partition),
        });
        output.WriteLine($"{oldLetter}: -> {moved.DriveLetter}:");
        Assert.Equal(newLetter, moved.DriveLetter);
        Assert.False(Directory.Exists($@"{oldLetter}:\"), "the old letter should be gone");
        Assert.Equal("stays", await File.ReadAllTextAsync($@"{newLetter}:\keep.txt"));

        var renamed = await ops.SetLabelAsync(new SetLabelRequest { DiskNumber = n, PartitionNumber = partition.Number, Label = "After" });
        Assert.Equal("After", renamed.Volume?.Label);
        Assert.Equal("After", new DriveInfo($"{newLetter}:").VolumeLabel);

        // Same label again is refused before anything is written.
        var ex = await Assert.ThrowsAsync<StorageOperationException>(() => ops.SetLabelAsync(new SetLabelRequest { DiskNumber = n, PartitionNumber = partition.Number, Label = "After" }));
        Assert.Contains("Refused before touching the disk", ex.Message, StringComparison.Ordinal);

        var hidden = await ops.ChangeDriveLetterAsync(new ChangeDriveLetterRequest { DiskNumber = n, PartitionNumber = partition.Number, NewDriveLetter = null });
        Assert.Null(hidden.DriveLetter);
        Assert.False(Directory.Exists($@"{newLetter}:\"));

        var back = await ops.ChangeDriveLetterAsync(new ChangeDriveLetterRequest { DiskNumber = n, PartitionNumber = partition.Number, NewDriveLetter = newLetter });
        Assert.Equal(newLetter, back.DriveLetter);
        Assert.Equal("stays", await File.ReadAllTextAsync($@"{newLetter}:\keep.txt"));
        Assert.Equal(0, vhd.Chkdsk(newLetter));
    }
}
