using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>Empty disks switch between GPT and MBR; a disk with a partition on it is never touched.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxConvertTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task GptToMbr_AndBack_OnlyWhileTheDiskIsEmpty()
    {
        using var vhd = TestVhd.Create(1 * GiB, output, "convert");
        var ops = vhd.Operations;
        var n = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(n, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);
        output.WriteLine("GPT: " + TestVhd.LayoutOf(disk));
        Assert.Equal(PartitionStyle.Mbr, ConvertDiskPlanner.Target(disk));

        var mbr = await ops.ConvertDiskAsync(new ConvertDiskRequest { DiskNumber = n, Style = PartitionStyle.Mbr, ExpectedDisk = DiskFingerprint.Of(disk) });
        output.WriteLine("MBR: " + TestVhd.LayoutOf(mbr));
        Assert.Equal(PartitionStyle.Mbr, mbr.Style);
        Assert.Empty(mbr.Partitions);

        // With a partition on it the disk is refused, and the partition is still there afterwards.
        var region = FreeSpaceCalculator.Compute(mbr).MaxBy(r => r.SizeBytes)!;
        var created = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = region.OffsetBytes, SizeBytes = 256 * MiB, FileSystem = "NTFS", Label = "Keep" });
        await File.WriteAllTextAsync($@"{created.DriveLetter}:\keep.txt", "stays");
        var refused = await Assert.ThrowsAsync<StorageOperationException>(() => ops.ConvertDiskAsync(new ConvertDiskRequest { DiskNumber = n, Style = PartitionStyle.Gpt }));
        Assert.Contains("Refused before touching the disk", refused.Message, StringComparison.Ordinal);
        Assert.Equal("stays", await File.ReadAllTextAsync($@"{created.DriveLetter}:\keep.txt"));

        await ops.DeletePartitionAsync(n, created.Number);
        disk = await vhd.WaitAsync(d => d.Partitions.Count == 0);
        var gpt = await ops.ConvertDiskAsync(new ConvertDiskRequest { DiskNumber = n, Style = PartitionStyle.Gpt });
        output.WriteLine("GPT again: " + TestVhd.LayoutOf(gpt));
        Assert.Equal(PartitionStyle.Gpt, gpt.Style);
        Assert.All(gpt.Partitions, p => Assert.Equal(PartitionKind.MicrosoftReserved, p.Kind));
    }
}
