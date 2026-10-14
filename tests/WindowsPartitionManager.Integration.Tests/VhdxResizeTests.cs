using System.Diagnostics;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>Shrink and extend through the Storage API on a throwaway VHDX, with data on the volume the whole time.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxResizeTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task ShrinkCreateInFreedSpaceDeleteAndExtendBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpm-resize-{Guid.NewGuid():N}.vhdx");
        var provider = new WmiStorageProvider();
        var operations = new WmiStorageOperations();

        using var vhd = VirtualDisk.Create(path, 2 * GiB);
        try
        {
            vhd.Attach();
            var diskNumber = vhd.GetDiskNumber();
            await WaitForDiskAsync(provider, diskNumber, _ => true);
            await operations.InitializeDiskAsync(diskNumber, PartitionStyle.Gpt);
            var disk = await WaitForDiskAsync(provider, diskNumber, d => d.Style == PartitionStyle.Gpt);

            // One NTFS partition filling the disk.
            var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
            var created = await operations.CreatePartitionAsync(new CreatePartitionRequest
            {
                DiskNumber = diskNumber,
                OffsetBytes = region.OffsetBytes,
                SizeBytes = region.SizeBytes,
                FileSystem = "NTFS",
                Label = "ShrinkMe",
            });
            output.WriteLine($"Created {created.DriveLetter}: {created.SizeBytes / MiB} MiB NTFS");
            var originalSize = created.SizeBytes;

            // Put real data on it so the shrink has to preserve something.
            var dataFile = Path.Combine($"{created.DriveLetter}:\\", "payload.bin");
            var payload = new byte[64 * MiB];
            Random.Shared.NextBytes(payload);
            await File.WriteAllBytesAsync(dataFile, payload);

            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Any(p => p.Number == created.Number && p.Volume?.UsedBytes >= 64 * MiB));
            var partition = disk.Partitions.Single(p => p.Number == created.Number);

            var limits = await provider.GetSupportedSizeAsync(diskNumber, partition.Number);
            Assert.NotNull(limits);
            output.WriteLine($"Windows limits: min {limits!.MinimumBytes / MiB} MiB, max {limits.MaximumBytes / MiB} MiB; used {partition.Volume!.UsedBytes / MiB} MiB");

            // Shrink to 1 GiB.
            var shrink = ResizePlanner.Normalize(new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = partition.Number, NewSizeBytes = 1 * GiB });
            var issues = ResizePlanner.Validate(disk, partition, shrink, limits);
            Assert.DoesNotContain(issues, i => i.IsError);

            var shrunk = await operations.ResizePartitionAsync(shrink, new Progress<string>(output.WriteLine));
            output.WriteLine($"After shrink: {shrunk.SizeBytes / MiB} MiB, volume {shrunk.Volume?.SizeBytes / MiB} MiB");
            Assert.InRange(shrunk.SizeBytes, 1 * GiB - 1 * MiB, 1 * GiB + 1 * MiB);
            Assert.Equal(payload, await File.ReadAllBytesAsync(dataFile));

            // The freed space must show up as a free region right after the partition, and Windows must agree.
            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Single(p => p.Number == created.Number).SizeBytes == shrunk.SizeBytes);
            var freed = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
            output.WriteLine($"Freed region: {freed.SizeBytes:N0} B at {freed.OffsetBytes / MiB} MiB (Windows largest free extent {disk.LargestFreeExtentBytes:N0} B)");
            Assert.InRange(freed.SizeBytes, originalSize - shrunk.SizeBytes - 2 * MiB, originalSize - shrunk.SizeBytes + 2 * MiB);

            // We align the usable end down to 1 MiB; Windows does not, so it may report up to 1 MiB more. Never less.
            Assert.True(freed.SizeBytes <= disk.LargestFreeExtentBytes, "our free region exceeds what Windows reports");
            Assert.True(disk.LargestFreeExtentBytes - freed.SizeBytes < 1 * MiB, "our free region is more than 1 MiB smaller than Windows'");

            // The point of the whole exercise: a new partition in the freed space.
            var second = await operations.CreatePartitionAsync(new CreatePartitionRequest
            {
                DiskNumber = diskNumber,
                OffsetBytes = freed.OffsetBytes,
                SizeBytes = freed.SizeBytes,
                FileSystem = "NTFS",
                Label = "NewHome",
            });
            output.WriteLine($"Created second partition {second.DriveLetter}: {second.SizeBytes / MiB} MiB");
            Assert.Equal("NTFS", second.Volume?.FileSystem);

            // Extending must be refused while the second partition is in the way...
            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Any(p => p.Number == second.Number));
            partition = disk.Partitions.Single(p => p.Number == created.Number);
            var blocked = ResizePlanner.Validate(disk, partition, new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = partition.Number, NewSizeBytes = originalSize });
            Assert.Contains(blocked, i => i.IsError);

            // ...and work again once it is gone.
            await operations.DeletePartitionAsync(diskNumber, second.Number);
            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.All(p => p.Number != second.Number));
            partition = disk.Partitions.Single(p => p.Number == created.Number);
            var max = ResizePlanner.MaximumSize(disk, partition);
            var extend = ResizePlanner.Normalize(new ResizePartitionRequest { DiskNumber = diskNumber, PartitionNumber = partition.Number, NewSizeBytes = max });
            Assert.DoesNotContain(ResizePlanner.Validate(disk, partition, extend), i => i.IsError);

            var extended = await operations.ResizePartitionAsync(extend, new Progress<string>(output.WriteLine));
            output.WriteLine($"After extend: {extended.SizeBytes / MiB} MiB");
            Assert.InRange(extended.SizeBytes, originalSize - 1 * MiB, originalSize + 1 * MiB);
            Assert.Equal(payload, await File.ReadAllBytesAsync(dataFile));
        }
        finally
        {
            try
            {
                vhd.Detach();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                output.WriteLine($"Detach failed: {ex.Message}");
            }

            vhd.Dispose();
            File.Delete(path);
        }
    }

    [AdminFact]
    public async Task ShrinkAndCreateInOneStep()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpm-shandcreate-{Guid.NewGuid():N}.vhdx");
        var provider = new WmiStorageProvider();
        var operations = new WmiStorageOperations();

        using var vhd = VirtualDisk.Create(path, 2 * GiB);
        try
        {
            vhd.Attach();
            var diskNumber = vhd.GetDiskNumber();
            await WaitForDiskAsync(provider, diskNumber, _ => true);
            await operations.InitializeDiskAsync(diskNumber, PartitionStyle.Gpt);
            var disk = await WaitForDiskAsync(provider, diskNumber, d => d.Style == PartitionStyle.Gpt);

            var region = FreeSpaceCalculator.Compute(disk).MaxBy(r => r.SizeBytes)!;
            var created = await operations.CreatePartitionAsync(new CreatePartitionRequest
            {
                DiskNumber = diskNumber,
                OffsetBytes = region.OffsetBytes,
                SizeBytes = region.SizeBytes,
                FileSystem = "NTFS",
                Label = "Source",
            });
            var originalSize = created.SizeBytes;

            var dataFile = Path.Combine($"{created.DriveLetter}:\\", "payload.bin");
            var payload = new byte[32 * MiB];
            Random.Shared.NextBytes(payload);
            await File.WriteAllBytesAsync(dataFile, payload);

            disk = await WaitForDiskAsync(provider, diskNumber, d => d.Partitions.Any(p => p.Number == created.Number && p.Volume?.UsedBytes >= 32 * MiB));
            var partition = disk.Partitions.Single(p => p.Number == created.Number);
            var limits = await provider.GetSupportedSizeAsync(diskNumber, partition.Number);

            var combined = ShrinkAndCreatePlanner.Normalize(new ShrinkAndCreateRequest
            {
                DiskNumber = diskNumber,
                PartitionNumber = partition.Number,
                NewSizeBytes = 1 * GiB,
                FileSystem = "NTFS",
                Label = "Freed",
            });

            Assert.DoesNotContain(ShrinkAndCreatePlanner.Validate(disk, partition, combined, limits), i => i.IsError);

            var shrunk = await operations.ResizePartitionAsync(new ResizePartitionRequest
            {
                DiskNumber = combined.DiskNumber,
                PartitionNumber = combined.PartitionNumber,
                NewSizeBytes = combined.NewSizeBytes,
            }, new Progress<string>(output.WriteLine));

            var createRequest = ShrinkAndCreatePlanner.CreateRequest(combined, disk, partition)!;
            var second = await operations.CreatePartitionAsync(createRequest, new Progress<string>(output.WriteLine));

            output.WriteLine($"Shrunk to {shrunk.SizeBytes / MiB} MiB; created {second.DriveLetter}: {second.SizeBytes / MiB} MiB");
            Assert.InRange(shrunk.SizeBytes, 1 * GiB - 1 * MiB, 1 * GiB + 1 * MiB);
            Assert.Equal("NTFS", second.Volume?.FileSystem);
            Assert.Equal("Freed", second.Volume?.Label);
            Assert.InRange(second.SizeBytes, originalSize - shrunk.SizeBytes - 2 * MiB, originalSize - shrunk.SizeBytes + 2 * MiB);
            Assert.Equal(payload, await File.ReadAllBytesAsync(dataFile));
        }
        finally
        {
            try
            {
                vhd.Detach();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                output.WriteLine($"Detach failed: {ex.Message}");
            }

            vhd.Dispose();
            File.Delete(path);
        }
    }

    private static async Task<Disk> WaitForDiskAsync(WmiStorageProvider provider, int diskNumber, Func<Disk, bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var disk = (await provider.GetDisksAsync()).FirstOrDefault(d => d.Number == diskNumber);

            // A test must never act on a real drive, whatever bug made it look at one.
            if (disk is not null && !(disk.BusType.Contains("virtual", StringComparison.OrdinalIgnoreCase) && disk.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !disk.IsSystem && !disk.IsBoot))
            {
                throw new InvalidOperationException($"Refusing to continue: disk {diskNumber} ({disk.FriendlyName}, {disk.BusType}) is not a throwaway virtual disk.");
            }

            if (disk is not null && condition(disk))
            {
                return disk;
            }

            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException($"Disk {diskNumber} did not reach the expected state within 30 s.");
            }

            await Task.Delay(500);
        }
    }
}
