using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>The MFT-based space analysis must find every folder, deep or small, and add sizes up correctly.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxSpaceTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task DeepAndSmallFolders_AreAllFound_AndSizesAddUp()
    {
        using var vhd = TestVhd.Create(1 * GiB, output, "space");
        var ops = vhd.Operations;
        var n = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(n, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);
        var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
        var created = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = region.OffsetBytes, SizeBytes = region.SizeBytes, FileSystem = "NTFS", Label = "Space" });
        var root = $@"{created.DriveLetter}:\";

        // A deep chain of tiny folders (their index stays inside the MFT record), a big file at the bottom,
        // and a wide folder with many small files.
        var deep = Path.Combine(root, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(deep);
        await File.WriteAllBytesAsync(Path.Combine(deep, "big.bin"), new byte[64 * MiB]);
        var wide = Path.Combine(root, "wide");
        Directory.CreateDirectory(wide);
        for (var i = 0; i < 300; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(wide, $"f{i:000}.bin"), new byte[16 * 1024]);
        }

        var entries = await new NtfsSpaceReader().ReadAsync(created.DriveLetter!.Value);
        var report = SpaceAnalyzer.Build(created.DriveLetter!.Value, entries);
        output.WriteLine($"{report.Files} files, {report.Folders} folders, {report.Root.SizeBytes / MiB} MiB; volume uses {created.Volume?.UsedBytes / MiB} MiB");

        // Nothing may end up at the root because its parent was missing from the listing.
        var a = report.Root.Folders.Single(f => f.Name == "a");
        var e = a.Folders.Single().Folders.Single().Folders.Single().Folders.Single();
        Assert.Equal(@"a\b\c\d\e", e.Path);
        Assert.Equal(@"a\b\c\d\e\big.bin", e.LargestFiles.Single().Path);
        Assert.InRange(a.SizeBytes, 64 * MiB, 65 * MiB);
        Assert.Equal(report.LargestFiles[0].Path, e.LargestFiles[0].Path);

        var wideFolder = report.Root.Folders.Single(f => f.Name == "wide");
        Assert.Equal(300, wideFolder.FileCount);
        Assert.InRange(wideFolder.SizeBytes, 300 * 16 * 1024UL, 300 * 20 * 1024UL);
        Assert.DoesNotContain(report.Root.LargestFiles, f => f.Name.StartsWith('f') && f.Name.EndsWith(".bin", StringComparison.Ordinal));

        // The total is close to what Windows reports as used (NTFS keeps some space it does not list per file).
        var used = (await vhd.Provider.GetDisksAsync()).Single(d => d.Number == n).Partitions.Single(p => p.Number == created.Number).Volume!.UsedBytes;
        Assert.InRange(report.Root.SizeBytes, used * 9 / 10, used + (8 * MiB));
    }
}
