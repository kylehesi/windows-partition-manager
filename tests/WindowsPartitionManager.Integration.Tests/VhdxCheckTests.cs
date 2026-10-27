using System.Diagnostics;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>File system checks on real volumes: scans, quick fix, and a full fix that clears the dirty bit.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxCheckTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task Scan_SpotFix_AndFullFix_OnNtfs_AndTheFullCheckOnExfat()
    {
        using var vhd = TestVhd.Create(2 * GiB, output, "check");
        var ops = vhd.Operations;
        var n = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(n, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);
        var start = FreeSpaceCalculator.Compute(disk)[0].OffsetBytes;
        var ntfs = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = start, SizeBytes = 1 * GiB, FileSystem = "NTFS", Label = "Ntfs" });
        var exfat = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = ntfs.EndBytes, SizeBytes = 512 * MiB, FileSystem = "exFAT", Label = "Stick" });
        await File.WriteAllTextAsync($@"{ntfs.DriveLetter}:\keep.txt", "stays");

        async Task<CheckResult> CheckAsync(Partition partition, CheckMode mode)
        {
            var result = await ops.CheckVolumeAsync(new CheckVolumeRequest { DiskNumber = n, PartitionNumber = partition.Number, Mode = mode }, new Progress<string>(output.WriteLine));
            output.WriteLine($"{partition.DriveLetter}: {mode} -> status {result.Code} ({result.Outcome})");
            return result;
        }

        Assert.Equal(CheckOutcome.Clean, (await CheckAsync(ntfs, CheckMode.Scan)).Outcome);
        Assert.Contains((await CheckAsync(ntfs, CheckMode.SpotFix)).Outcome, new[] { CheckOutcome.Clean, CheckOutcome.Fixed });

        // An unclean shutdown leaves the dirty bit; the full check clears it.
        var fsutil = Process.Start(new ProcessStartInfo("fsutil.exe", $"dirty set {ntfs.DriveLetter}:") { UseShellExecute = false, CreateNoWindow = true })!;
        await fsutil.WaitForExitAsync();
        Assert.True(VolumeDirtyBit.Query(ntfs.DriveLetter!.Value, out _));
        Assert.Contains((await CheckAsync(ntfs, CheckMode.OfflineScanAndFix)).Outcome, new[] { CheckOutcome.Clean, CheckOutcome.Fixed });
        disk = await vhd.WaitAsync(d => d.Partitions.Any(p => p.Number == ntfs.Number && p.Volume is { IsDirty: false }));
        Assert.False(VolumeDirtyBit.Query(ntfs.DriveLetter!.Value, out _), "the full check should clear the dirty bit");
        Assert.Equal("stays", await File.ReadAllTextAsync($@"{ntfs.DriveLetter}:\keep.txt"));

        // exFAT has no online scan; the full check works.
        var refused = await Assert.ThrowsAsync<StorageOperationException>(() => CheckAsync(exfat, CheckMode.Scan));
        Assert.Contains("Refused before touching the disk", refused.Message, StringComparison.Ordinal);
        Assert.Contains((await CheckAsync(exfat, CheckMode.OfflineScanAndFix)).Outcome, new[] { CheckOutcome.Clean, CheckOutcome.Fixed });
    }
}
