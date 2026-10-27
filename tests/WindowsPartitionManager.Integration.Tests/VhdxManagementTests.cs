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

/// <summary>The user-facing virtual disk flow: create, use, detach, attach again, detach.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxManagementTests(ITestOutputHelper output)
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    [AdminFact]
    public async Task Create_Detach_Attach_KeepsTheFiles_AndTheDiskKnowsItsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpm-manage-{Guid.NewGuid():N}.vhdx");
        var provider = new WmiStorageProvider();
        var storage = new WmiStorageOperations();
        var vhds = new VirtualDiskOperations(storage);
        TestVhd.Trace($"manage create {Path.GetFileName(path)}");
        try
        {
            var number = await vhds.CreateAsync(new CreateVirtualDiskRequest { Path = path, SizeBytes = 1 * GiB, Style = PartitionStyle.Gpt });
            var disk = await WaitAsync(provider, number, d => d.Style == PartitionStyle.Gpt);
            output.WriteLine($"Created disk {number}: {disk.FriendlyName}, {disk.BusType}, file {disk.VirtualDiskPath}");
            Assert.Equal(Path.GetFullPath(path), disk.VirtualDiskPath, ignoreCase: true);
            Assert.True(VirtualDiskPlanner.CanDetach(disk));

            var refused = await Assert.ThrowsAsync<StorageOperationException>(() => vhds.CreateAsync(new CreateVirtualDiskRequest { Path = path, SizeBytes = 1 * GiB }));
            Assert.Contains("already exists", refused.Message, StringComparison.Ordinal);

            var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
            var created = await storage.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = number, OffsetBytes = region.OffsetBytes, SizeBytes = region.SizeBytes, FileSystem = "NTFS", Label = "Practice" });
            await File.WriteAllTextAsync($@"{created.DriveLetter}:\keep.txt", "stays");

            disk = await WaitAsync(provider, number, d => d.Partitions.Any(p => p.Number == created.Number));
            await vhds.DetachAsync(new DetachVirtualDiskRequest { DiskNumber = number, ExpectedDisk = DiskFingerprint.Of(disk) });
            Assert.DoesNotContain(await provider.GetDisksAsync(), d => d.VirtualDiskPath is { } p && p.Equals(path, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(path), "detaching must keep the file");

            // Attached by the user, the disk gets its drive letter back by itself.
            var again = await vhds.AttachAsync(path);
            disk = await WaitAsync(provider, again, d => d.Partitions.Any(p => p.Number == created.Number && p.DriveLetter is not null));
            var letter = disk.Partitions.Single(p => p.Number == created.Number).DriveLetter!.Value;
            output.WriteLine($"Re-attached as disk {again}, {letter}:");
            Assert.Equal("stays", await File.ReadAllTextAsync($@"{letter}:\keep.txt"));

            await vhds.DetachAsync(new DetachVirtualDiskRequest { DiskNumber = again });
        }
        finally
        {
            try
            {
                using var vhd = VirtualDisk.Open(path);
                vhd.Detach();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                // Already detached, or never created.
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            TestVhd.Trace("manage done");
        }
    }

    private static async Task<Disk> WaitAsync(WmiStorageProvider provider, int number, Func<Disk, bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var disk = (await provider.GetDisksAsync()).FirstOrDefault(d => d.Number == number);

            // A test must never act on a real drive, whatever bug made it look at one.
            if (disk is not null && disk.VirtualDiskPath is null)
            {
                throw new InvalidOperationException($"Refusing to continue: disk {number} ({disk.FriendlyName}, {disk.BusType}) is not a virtual disk.");
            }

            if (disk is not null && condition(disk))
            {
                return disk;
            }

            if (stopwatch.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException($"Disk {number} did not reach the expected state within 60 s.");
            }

            await Task.Delay(500);
        }
    }
}
