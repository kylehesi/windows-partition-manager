using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>TRIM and defragmentation on real volumes; the data must be untouched afterwards.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxOptimizeTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task Trim_AndDefrag_OnNtfs_DefragOnFat32_KeepTheFiles()
    {
        using var vhd = TestVhd.Create(2 * GiB, output, "optimize");
        var ops = vhd.Operations;
        var n = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(n, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);
        var start = FreeSpaceCalculator.Compute(disk)[0].OffsetBytes;
        var ntfs = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = start, SizeBytes = 1 * GiB, FileSystem = "NTFS", Label = "Ntfs" });
        var fat = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = ntfs.EndBytes, SizeBytes = 512 * MiB, FileSystem = "FAT32", Label = "FAT" });

        // Some fragmented data to work on: files written interleaved, then every other one deleted.
        var files = TestData.WriteRandomFiles($@"{ntfs.DriveLetter}:\", "data", 200 * (long)MiB, seed: 11);
        foreach (var path in files.Keys.Where((_, i) => i % 2 == 0).ToList())
        {
            File.Delete(Path.Combine($@"{ntfs.DriveLetter}:\", path));
            files.Remove(path);
        }

        async Task OptimizeAsync(Partition partition, OptimizeMode mode)
        {
            var started = DateTime.UtcNow;
            await ops.OptimizeVolumeAsync(new OptimizeVolumeRequest { DiskNumber = n, PartitionNumber = partition.Number, Mode = mode }, new Progress<string>(output.WriteLine));
            output.WriteLine($"{partition.DriveLetter}: {mode} done in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
        }

        await OptimizeAsync(ntfs, OptimizeMode.ReTrim);
        await OptimizeAsync(ntfs, OptimizeMode.Defrag);
        await OptimizeAsync(fat, OptimizeMode.Defrag);

        Assert.Empty(TestData.Verify(ntfs.DriveLetter!.Value, files));
        Assert.Equal(0, vhd.Chkdsk(ntfs.DriveLetter!.Value));

        // exFAT and anything else Windows cannot optimize is refused before the disk is touched.
        var refused = await Assert.ThrowsAsync<StorageOperationException>(() => ops.OptimizeVolumeAsync(new OptimizeVolumeRequest { DiskNumber = n, PartitionNumber = fat.Number, Mode = OptimizeMode.ReTrim }));
        Assert.Contains("Refused before touching the disk", refused.Message, StringComparison.Ordinal);
    }
}
