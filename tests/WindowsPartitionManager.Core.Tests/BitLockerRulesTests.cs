using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class BitLockerRulesTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static (Disk Disk, Partition Partition) Layout(BitLockerStatus? bitLocker, string fs = "NTFS")
    {
        var partition = new Partition
        {
            Number = 2,
            OffsetBytes = 1 * MiB,
            SizeBytes = 50 * GiB,
            Kind = PartitionKind.Basic,
            DriveLetter = 'E',
            Volume = new Volume { Path = "v", FileSystem = fs, SizeBytes = 50 * GiB, FreeBytes = 40 * GiB, DriveLetter = 'E', BitLocker = bitLocker },
        };
        return (new Disk
        {
            Number = 1,
            FriendlyName = "Test",
            BusType = "SATA",
            SizeBytes = 100 * GiB,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 4096,
            Style = PartitionStyle.Gpt,
            Partitions = [partition],
        }, partition);
    }

    private static ResizePartitionRequest Shrink() => new() { DiskNumber = 1, PartitionNumber = 2, NewSizeBytes = 30 * GiB };

    [Fact]
    public void Unencrypted_OrUnknown_ChangesNothing()
    {
        var (disk, partition) = Layout(null);
        Assert.DoesNotContain(ResizePlanner.Validate(disk, partition, Shrink()), i => i.Message.Contains("BitLocker", StringComparison.Ordinal));

        (disk, partition) = Layout(new BitLockerStatus(EncryptionState.NotEncrypted, false, false));
        Assert.DoesNotContain(ResizePlanner.Validate(disk, partition, Shrink()), i => i.Message.Contains("BitLocker", StringComparison.Ordinal));
    }

    [Fact]
    public void Encrypted_CanBeResized_WithARecoveryKeyReminder()
    {
        var (disk, partition) = Layout(new BitLockerStatus(EncryptionState.Encrypted, true, false));
        var issues = ResizePlanner.Validate(disk, partition, Shrink());

        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("recovery key", StringComparison.Ordinal));
    }

    [Fact]
    public void EncryptedWithProtectionOff_NeedsNoRecoveryKeyReminder()
    {
        // Device encryption waiting for activation: encrypted, but Windows holds the key itself.
        var (disk, partition) = Layout(new BitLockerStatus(EncryptionState.Encrypted, ProtectionOn: false, Locked: false));
        Assert.DoesNotContain(ResizePlanner.Validate(disk, partition, Shrink()), i => i.Message.Contains("BitLocker", StringComparison.Ordinal));
    }

    [Fact]
    public void WhileBitLockerConverts_ResizeWaits()
    {
        var (disk, partition) = Layout(new BitLockerStatus(EncryptionState.Encrypting, true, false, 42));
        Assert.Contains(ResizePlanner.Validate(disk, partition, Shrink()), i => i.IsError && i.Message.Contains("42", StringComparison.Ordinal));
    }

    [Fact]
    public void Locked_RefusesEverythingThatReadsIt_AndWarnsBeforeItIsDestroyed()
    {
        // A locked volume reports no file system; the BitLocker reason must win over "unknown file system".
        var (disk, partition) = Layout(new BitLockerStatus(EncryptionState.Encrypted, true, true), fs: string.Empty);

        Assert.Contains(ResizePlanner.Validate(disk, partition, Shrink()), i => i.IsError && i.Message.Contains("locked", StringComparison.Ordinal));
        Assert.Contains(CheckPlanner.Validate(disk, partition, new CheckVolumeRequest { DiskNumber = 1, PartitionNumber = 2, Mode = CheckMode.Scan }), i => i.IsError && i.Message.Contains("locked", StringComparison.Ordinal));
        Assert.Contains(OptimizePlanner.Validate(disk, partition, new OptimizeVolumeRequest { DiskNumber = 1, PartitionNumber = 2, Mode = OptimizeMode.ReTrim }), i => i.IsError && i.Message.Contains("locked", StringComparison.Ordinal));

        var delete = PartitionPlanner.ValidateDelete(disk, partition);
        Assert.DoesNotContain(delete, i => i.IsError);
        Assert.Contains(delete, i => i.Message.Contains("lost for good", StringComparison.Ordinal));
    }
}
