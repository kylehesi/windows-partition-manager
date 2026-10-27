using System.Globalization;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.Tests;

public sealed class OptimizeVolumeViewModelTests
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

    [Theory]
    [InlineData(MediaType.Ssd, OptimizeMode.ReTrim)]
    [InlineData(MediaType.Hdd, OptimizeMode.Defrag)]
    public void TheRecommendedModeComesFirst_AndIsMarked(MediaType media, OptimizeMode expected)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var (disk, partition) = Layout();
        var vm = new OptimizeVolumeViewModel(new OptimizeOperations(), disk, partition, media);

        Assert.Equal(expected, vm.Selected.Mode);
        Assert.EndsWith("(recommended)", vm.Selected.Name, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Issues, i => i.IsError);
    }

    [Fact]
    public void DefragOnSsd_IsExplained_ThenRuns()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CoreText.Culture = CultureInfo.InvariantCulture; // other tests switch the app to Turkish
        var (disk, partition) = Layout();
        var operations = new OptimizeOperations();
        var vm = new OptimizeVolumeViewModel(operations, disk, partition, MediaType.Ssd);

        vm.Selected = vm.Modes.Single(m => m.Mode == OptimizeMode.Defrag);
        Assert.Contains(vm.Issues, i => i.Message.Contains("wear", StringComparison.Ordinal));
        Assert.True(vm.CanRun);

        vm.RunCommand.Execute(null);

        Assert.Equal([OptimizeMode.Defrag], operations.Modes);
        Assert.StartsWith("Done: Defragment", vm.Result, StringComparison.Ordinal);
    }

    private sealed class OptimizeOperations : IStorageOperations
    {
        public List<OptimizeMode> Modes { get; } = [];

        public Task OptimizeVolumeAsync(OptimizeVolumeRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Modes.Add(request.Mode);
            return Task.CompletedTask;
        }

        public Task<CheckResult> CheckVolumeAsync(CheckVolumeRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task InitializeDiskAsync(int diskNumber, PartitionStyle style, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> CreatePartitionAsync(CreatePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> FormatPartitionAsync(FormatPartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeletePartitionAsync(DeletePartitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> ResizePartitionAsync(ResizePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> ChangeDriveLetterAsync(ChangeDriveLetterRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> SetLabelAsync(SetLabelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Disk> ConvertDiskAsync(ConvertDiskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Disk> SetDiskStateAsync(SetDiskStateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
