using System.Globalization;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.Tests;

public sealed class VolumePropertiesViewModelTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static (Disk Disk, Partition Partition) Layout(bool boot = false)
    {
        var partition = new Partition
        {
            Number = 2,
            OffsetBytes = 1024 * 1024,
            SizeBytes = 10 * GiB,
            Kind = PartitionKind.Basic,
            TypeDescription = "Basic data",
            DriveLetter = boot ? 'C' : 'E',
            IsBoot = boot,
            Volume = new Volume { Path = "v", FileSystem = "NTFS", Label = "Data", SizeBytes = 10 * GiB, FreeBytes = 5 * GiB, DriveLetter = boot ? 'C' : 'E' },
        };
        var disk = new Disk
        {
            Number = 1,
            FriendlyName = "Test",
            BusType = "SATA",
            SizeBytes = 100 * GiB,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 4096,
            Style = PartitionStyle.Gpt,
            Partitions = [partition],
        };
        return (disk, partition);
    }

    private static VolumePropertiesViewModel Create(RecordingOperations operations, bool boot = false)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var (disk, partition) = Layout(boot);
        return new VolumePropertiesViewModel(operations, disk, partition, new HashSet<char> { 'C', 'E', 'F' }, _ => true);
    }

    [Fact]
    public void Letters_OfferNoLetterTheCurrentOneAndFreeOnes_NotTakenOnes()
    {
        var vm = Create(new RecordingOperations());

        Assert.Equal("No drive letter", vm.Letters[0]);
        Assert.Contains("E:", vm.Letters);
        Assert.Contains("G:", vm.Letters);
        Assert.DoesNotContain("F:", vm.Letters);
        Assert.Equal("E:", vm.SelectedLetter);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task ChangingBoth_RenamesFirstThenMovesTheLetter()
    {
        var operations = new RecordingOperations();
        var vm = Create(operations);
        vm.Label = "Backup";
        vm.SelectedLetter = "K:";

        Assert.True(vm.CanApply);
        Assert.Contains("\"Backup\"", vm.Summary, StringComparison.Ordinal);
        Assert.Contains("K:", vm.Summary, StringComparison.Ordinal);

        vm.ApplyCommand.Execute(null);
        await operations.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["label:Backup", "letter:K"], operations.Calls);
        Assert.True(vm.IsDone);
    }

    [Fact]
    public void WindowsDrive_KeepsItsLetter_ButCanBeRenamed()
    {
        var vm = Create(new RecordingOperations(), boot: true);

        Assert.False(vm.CanChangeLetter);
        Assert.True(vm.CanRename);
        Assert.Contains("only the label", vm.Summary, StringComparison.Ordinal);

        vm.Label = "System";
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void TakenLetter_BlocksApply()
    {
        var vm = Create(new RecordingOperations());
        vm.SelectedLetter = "F:";

        Assert.Contains(vm.Issues, i => i.IsError);
        Assert.False(vm.CanApply);
    }

    private sealed class RecordingOperations : IStorageOperations
    {
        public List<string> Calls { get; } = [];

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Partition> SetLabelAsync(SetLabelRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("label:" + request.Label);
            return Task.FromResult(Layout().Partition);
        }

        public Task<Partition> ChangeDriveLetterAsync(ChangeDriveLetterRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("letter:" + request.NewDriveLetter);
            Finished.TrySetResult();
            return Task.FromResult(Layout().Partition);
        }

        public Task InitializeDiskAsync(int diskNumber, PartitionStyle style, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> CreatePartitionAsync(CreatePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> FormatPartitionAsync(FormatPartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeletePartitionAsync(DeletePartitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> ResizePartitionAsync(ResizePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
