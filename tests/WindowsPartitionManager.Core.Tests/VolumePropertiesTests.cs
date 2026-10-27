using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class VolumePropertiesTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Disk Disk(Partition partition, string bus = "SATA", bool readOnly = false) => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = bus,
        SizeBytes = 100 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        IsReadOnly = readOnly,
        Partitions = [partition],
    };

    private static Partition Data(char? letter = 'E', string fs = "NTFS", string? label = "Data", bool boot = false) => new()
    {
        Number = 2,
        OffsetBytes = 1 * MiB,
        SizeBytes = 10 * GiB,
        Kind = PartitionKind.Basic,
        TypeDescription = "Basic data",
        DriveLetter = letter,
        IsBoot = boot,
        Volume = new Volume { Path = "v", FileSystem = fs, Label = label, SizeBytes = 10 * GiB, FreeBytes = 5 * GiB, DriveLetter = letter },
    };

    private static ChangeDriveLetterRequest Letter(char? letter) => new() { DiskNumber = 1, PartitionNumber = 2, NewDriveLetter = letter };

    private static SetLabelRequest Label(string label) => new() { DiskNumber = 1, PartitionNumber = 2, Label = label };

    [Fact]
    public void ChangeToFreeLetter_IsAllowed_WithAWarningAboutTheOldLetter()
    {
        var partition = Data();
        var issues = DriveLetterPlanner.Validate(Disk(partition), partition, Letter('F'), new HashSet<char> { 'C', 'E' });

        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("E:", StringComparison.Ordinal));
    }

    [Fact]
    public void LetterInUse_OrSameLetter_OrOutOfRange_IsRefused()
    {
        var partition = Data();
        var disk = Disk(partition);
        var inUse = new HashSet<char> { 'C', 'E', 'G' };

        Assert.Contains(DriveLetterPlanner.Validate(disk, partition, Letter('g'), inUse), i => i.IsError);
        Assert.Contains(DriveLetterPlanner.Validate(disk, partition, Letter('E'), inUse), i => i.IsError);
        Assert.Contains(DriveLetterPlanner.Validate(disk, partition, Letter('B'), inUse), i => i.IsError);
    }

    [Fact]
    public void WindowsPartition_KeepsItsLetter()
    {
        var partition = Data(letter: 'C', boot: true);
        var disk = Disk(partition);

        Assert.False(DriveLetterPlanner.CanChange(disk, partition));
        Assert.Contains(DriveLetterPlanner.Validate(disk, partition, Letter('K')), i => i.IsError);
        Assert.Contains(DriveLetterPlanner.Validate(disk, partition, Letter(null)), i => i.IsError);
    }

    [Fact]
    public void RemovingALetter_WarnsThatTheDriveDisappears_AndNeedsALetter()
    {
        var partition = Data();
        var issues = DriveLetterPlanner.Validate(Disk(partition), partition, Letter(null));
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Equal(2, issues.Count(i => i.Severity == IssueSeverity.Warning));

        var noLetter = Data(letter: null);
        Assert.Contains(DriveLetterPlanner.Validate(Disk(noLetter), noLetter, Letter(null)), i => i.IsError);
    }

    [Fact]
    public void AssigningALetterToAPartitionWithoutOne_HasNoWarning()
    {
        var partition = Data(letter: null);
        Assert.Empty(DriveLetterPlanner.Validate(Disk(partition), partition, Letter('H')));
    }

    [Fact]
    public void ReadOnlyDisk_RefusesBothChanges()
    {
        var partition = Data();
        var disk = Disk(partition, readOnly: true);

        Assert.Contains(DriveLetterPlanner.Validate(disk, partition, Letter('F')), i => i.IsError);
        Assert.Contains(LabelPlanner.Validate(disk, partition, Label("New")), i => i.IsError);
    }

    [Fact]
    public void UsbDisk_GetsTheUnplugWarning()
    {
        var partition = Data();
        Assert.Contains(DriveLetterPlanner.Validate(Disk(partition, bus: "USB"), partition, Letter('F')), i => i.Message.Contains("USB", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NTFS", 32)]
    [InlineData("ReFS", 32)]
    [InlineData("exFAT", 15)]
    [InlineData("FAT32", 11)]
    public void LabelLength_FollowsTheFileSystem(string fs, int limit)
    {
        var partition = Data(fs: fs);
        var disk = Disk(partition);

        Assert.Equal(limit, LabelPlanner.MaximumLength(fs));
        Assert.DoesNotContain(LabelPlanner.Validate(disk, partition, Label(new string('A', limit))), i => i.IsError);
        Assert.Contains(LabelPlanner.Validate(disk, partition, Label(new string('A', limit + 1))), i => i.IsError);
    }

    [Fact]
    public void FatLabels_RefuseForbiddenCharacters_NtfsAllowsThem()
    {
        var fat = Data(fs: "FAT32");
        Assert.Contains(LabelPlanner.Validate(Disk(fat), fat, Label("A+B")), i => i.IsError);

        var ntfs = Data();
        Assert.DoesNotContain(LabelPlanner.Validate(Disk(ntfs), ntfs, Label("A+B")), i => i.IsError);
    }

    [Fact]
    public void Label_CanBeCleared_ButNotSetToTheSameValue()
    {
        var partition = Data(label: "Data");
        var disk = Disk(partition);

        Assert.Empty(LabelPlanner.Validate(disk, partition, Label(string.Empty)));
        Assert.Contains(LabelPlanner.Validate(disk, partition, Label("Data")), i => i.IsError);
    }

    [Fact]
    public void WindowsVolume_CanBeRenamed()
    {
        var partition = Data(letter: 'C', boot: true);
        var disk = Disk(partition);

        Assert.True(LabelPlanner.CanRename(disk, partition));
        Assert.Empty(LabelPlanner.Validate(disk, partition, Label("System")));
    }
}
