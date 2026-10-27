using System.Globalization;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.Tests;

public sealed class CheckVolumeViewModelTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static (Disk Disk, Partition Partition) Layout()
    {
        var partition = new Partition
        {
            Number = 2,
            OffsetBytes = 1024 * 1024,
            SizeBytes = 10 * GiB,
            Kind = PartitionKind.Basic,
            DriveLetter = 'E',
            Volume = new Volume { Path = "v", FileSystem = "NTFS", SizeBytes = 10 * GiB, FreeBytes = 5 * GiB },
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

    [Fact]
    public void Scan_RunsWithoutAConfirmation_AndAFoundProblemSelectsTheQuickFix()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CoreText.Culture = CultureInfo.InvariantCulture; // other tests switch the app to Turkish
        var (disk, partition) = Layout();
        var operations = new CheckOperations(7); // scan found problems that need a spot fix
        var asked = 0;
        var vm = new CheckVolumeViewModel(operations, disk, partition, _ => { asked++; return true; });

        Assert.Equal(CheckMode.Scan, vm.Selected.Mode);
        vm.RunCommand.Execute(null);

        Assert.Equal(0, asked);
        Assert.Equal([CheckMode.Scan], operations.Modes);
        Assert.False(vm.ResultIsGood);
        Assert.Contains("quick fix", vm.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CheckMode.SpotFix, vm.Selected.Mode);
    }

    [Fact]
    public void Fix_AsksFirst_AndDoesNothingWhenDeclined()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var (disk, partition) = Layout();
        var operations = new CheckOperations(0);
        var vm = new CheckVolumeViewModel(operations, disk, partition, _ => false);

        vm.Selected = vm.Modes.Single(m => m.Mode == CheckMode.OfflineScanAndFix);
        vm.RunCommand.Execute(null);

        Assert.Empty(operations.Modes);
    }

    private sealed class CheckOperations(uint code) : IStorageOperations
    {
        public List<CheckMode> Modes { get; } = [];

        public Task<CheckResult> CheckVolumeAsync(CheckVolumeRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Modes.Add(request.Mode);
            return Task.FromResult(CheckResult.FromCode(code));
        }

        public Task OptimizeVolumeAsync(OptimizeVolumeRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task InitializeDiskAsync(int diskNumber, PartitionStyle style, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> CreatePartitionAsync(CreatePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> FormatPartitionAsync(FormatPartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeletePartitionAsync(DeletePartitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> ResizePartitionAsync(ResizePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> ChangeDriveLetterAsync(ChangeDriveLetterRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> SetLabelAsync(SetLabelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Disk> SetDiskStateAsync(SetDiskStateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
