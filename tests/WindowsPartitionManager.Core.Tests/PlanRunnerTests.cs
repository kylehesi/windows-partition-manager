using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class PlanRunnerTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Disk Disk(params Partition[] partitions) => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = "SATA",
        SizeBytes = 100 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        Partitions = partitions,
    };

    private static Partition Data(int number, ulong offset, ulong size) => new()
    {
        Number = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Kind = PartitionKind.Basic,
        TypeDescription = "Basic data",
        Volume = new Volume { Path = $"v{number}", FileSystem = "NTFS", SizeBytes = size, FreeBytes = size },
    };

    [Fact]
    public async Task EachStep_UsesTheRealPartitionNumber_AndFreshFingerprints()
    {
        // Windows numbered the partition 7, the plan only knows where it starts.
        var simulated = new SimulatedDisks(Disk(Data(7, 1 * MiB, 10 * GiB)));
        PlannedOperation[] steps = [new PlannedResize(1, 1 * MiB, 5 * GiB), new PlannedFormat(1, 1 * MiB, "exFAT", "X")];

        var result = await PlanRunner.RunAsync(simulated.Disks, steps, simulated, simulated);

        Assert.True(result.Succeeded);
        Assert.Equal(["resize #7 to 5 GiB", "format #7 exFAT"], simulated.Calls);
        Assert.All(simulated.Fingerprints, f => Assert.Equal(7, f!.Number));
    }

    [Fact]
    public async Task ADiskThatChangedOutsideThePlan_StopsTheRun_BeforeTheStep()
    {
        var simulated = new SimulatedDisks(Disk(Data(1, 1 * MiB, 10 * GiB)));
        PlannedOperation[] steps = [new PlannedDelete(1, 1 * MiB)];
        var planned = simulated.Disks;

        // Someone resized the partition in Disk Management after the plan was made.
        simulated.Current = Disk(Data(1, 1 * MiB, 8 * GiB));

        var result = await PlanRunner.RunAsync(planned, steps, simulated, simulated);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.CompletedSteps);
        Assert.Empty(simulated.Calls);
        Assert.Contains("Step 1 stopped", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingStep_StopsTheRest()
    {
        var simulated = new SimulatedDisks(Disk(Data(1, 1 * MiB, 10 * GiB))) { FailOn = "format" };
        PlannedOperation[] steps = [new PlannedFormat(1, 1 * MiB, "exFAT", "X"), new PlannedDelete(1, 1 * MiB)];

        var result = await PlanRunner.RunAsync(simulated.Disks, steps, simulated, simulated);

        Assert.Equal(0, result.CompletedSteps);
        Assert.Contains("Windows said no.", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(simulated.Calls, c => c.StartsWith("delete", StringComparison.Ordinal));
    }

    /// <summary>A disk that changes the way the real one would, plus a record of every call.</summary>
    private sealed class SimulatedDisks(Disk start) : IStorageProvider, IStorageOperations
    {
        public Disk Current { get; set; } = start;

        public IReadOnlyList<Disk> Disks => [Current];

        public List<string> Calls { get; } = [];

        public List<PartitionFingerprint?> Fingerprints { get; } = [];

        public string? FailOn { get; init; }

        public Task<IReadOnlyList<Disk>> GetDisksAsync(CancellationToken cancellationToken = default) => Task.FromResult(Disks);

        public Task<Partition> ResizePartitionAsync(ResizePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Record($"resize #{request.PartitionNumber} to {request.NewSizeBytes / GiB} GiB", request.ExpectedPartition);
            Current = Current with { Partitions = [.. Current.Partitions.Select(p => p.Number == request.PartitionNumber ? p with { SizeBytes = request.NewSizeBytes } : p)] };
            return Task.FromResult(Current.Partitions[0]);
        }

        public Task<Partition> FormatPartitionAsync(FormatPartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Record($"format #{request.PartitionNumber} {request.FileSystem}", request.ExpectedPartition);
            return Task.FromResult(Current.Partitions[0]);
        }

        public Task DeletePartitionAsync(DeletePartitionRequest request, CancellationToken cancellationToken = default)
        {
            Record($"delete #{request.PartitionNumber}", request.ExpectedPartition);
            Current = Current with { Partitions = [] };
            return Task.CompletedTask;
        }

        public Task<Partition> CreatePartitionAsync(CreatePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task InitializeDiskAsync(int diskNumber, PartitionStyle style, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> ChangeDriveLetterAsync(ChangeDriveLetterRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Partition> SetLabelAsync(SetLabelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CheckResult> CheckVolumeAsync(CheckVolumeRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task OptimizeVolumeAsync(OptimizeVolumeRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Disk> ConvertDiskAsync(ConvertDiskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Disk> SetDiskStateAsync(SetDiskStateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private void Record(string call, PartitionFingerprint? fingerprint)
        {
            if (FailOn is { } fail && call.StartsWith(fail, StringComparison.Ordinal))
            {
                throw new StorageOperationException("Test", 0, "Windows said no.");
            }

            Calls.Add(call);
            Fingerprints.Add(fingerprint);
        }
    }
}
