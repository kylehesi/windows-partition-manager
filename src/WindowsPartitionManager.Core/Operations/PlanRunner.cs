using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>How far a plan got: the steps that ran, and why it stopped when it did not finish.</summary>
public sealed record PlanRunResult(int CompletedSteps, int TotalSteps, string? Error)
{
    public bool Succeeded => Error is null && CompletedSteps == TotalSteps;
}

/// <summary>Progress of a running plan: which step, out of how many, and what it does.</summary>
public sealed record PlanProgress(int Step, int TotalSteps, string Description, string? Detail = null);

/// <summary>
/// Runs a plan one step at a time. Before each step the disk is read again and compared with
/// what the plan expects at that point; the step's partition is then found by where it starts,
/// and the request is built with that partition's real number and fresh fingerprints, so the
/// operations layer checks everything once more. The first mismatch or failure stops the run;
/// later steps are left untouched.
/// </summary>
public static class PlanRunner
{
    /// <summary>Windows may round a size it applies; a difference this small is not a different layout.</summary>
    private const ulong SizeTolerance = 1024 * 1024;

    public static async Task<PlanRunResult> RunAsync(
        IReadOnlyList<Disk> startingDisks,
        IReadOnlyList<PlannedOperation> steps,
        IStorageProvider storage,
        IStorageOperations operations,
        IProgress<PlanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startingDisks);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(operations);

        var expected = startingDisks.ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[i];
            var plannedBefore = expected.First(d => d.Number == step.DiskNumber);
            var description = step.Describe(plannedBefore);
            progress?.Report(new PlanProgress(i + 1, steps.Count, description));

            var fresh = (await storage.GetDisksAsync(cancellationToken)).FirstOrDefault(d => d.Number == step.DiskNumber);
            if (Difference(fresh, plannedBefore) is { } difference)
            {
                return new PlanRunResult(i, steps.Count, CoreText.Format("Queue_LayoutDiffers", i + 1, difference));
            }

            try
            {
                await RunStepAsync(fresh!, step, operations, new Progress<string>(s => progress?.Report(new PlanProgress(i + 1, steps.Count, description, s))), cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return new PlanRunResult(i, steps.Count, ex.Message);
            }

            expected = [.. expected.Select(d => d.Number == step.DiskNumber ? LayoutSimulator.Apply(plannedBefore, step) : d)];
        }

        return new PlanRunResult(steps.Count, steps.Count, null);
    }

    /// <summary>Null when the real disk has the partitions the plan expects (same starts, sizes within a megabyte).</summary>
    public static string? Difference(Disk? real, Disk planned)
    {
        ArgumentNullException.ThrowIfNull(planned);
        if (real is null)
        {
            return CoreText.Format("Guard_DiskGone", string.Empty).Trim();
        }

        if (real.Partitions.Count != planned.Partitions.Count)
        {
            return $"{real.Partitions.Count} ≠ {planned.Partitions.Count}";
        }

        foreach (var want in planned.Partitions)
        {
            var have = real.Partitions.FirstOrDefault(p => p.OffsetBytes == want.OffsetBytes);
            var difference = have is null ? ulong.MaxValue : have.SizeBytes > want.SizeBytes ? have.SizeBytes - want.SizeBytes : want.SizeBytes - have.SizeBytes;
            if (difference > SizeTolerance)
            {
                return Formatting.ByteSize.Format(want.OffsetBytes);
            }
        }

        return null;
    }

    private static Task RunStepAsync(Disk fresh, PlannedOperation step, IStorageOperations operations, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var disk = DiskFingerprint.Of(fresh);
        if (step is PlannedCreate create)
        {
            return operations.CreatePartitionAsync(create.ToRequest() with { ExpectedDisk = disk }, progress, cancellationToken);
        }

        var offset = step switch
        {
            PlannedDelete d => d.OffsetBytes,
            PlannedResize r => r.OffsetBytes,
            PlannedFormat f => f.OffsetBytes,
            _ => throw new ArgumentException($"Unknown planned operation {step.GetType().Name}.", nameof(step)),
        };
        var target = fresh.Partitions.First(p => p.OffsetBytes == offset); // Difference() already proved it is there
        var partition = PartitionFingerprint.Of(target);

        return step switch
        {
            PlannedDelete => operations.DeletePartitionAsync(new DeletePartitionRequest { DiskNumber = fresh.Number, PartitionNumber = target.Number, ExpectedDisk = disk, ExpectedPartition = partition }, cancellationToken),
            PlannedResize r => operations.ResizePartitionAsync(new ResizePartitionRequest { DiskNumber = fresh.Number, PartitionNumber = target.Number, NewSizeBytes = r.NewSizeBytes, ExpectedDisk = disk, ExpectedPartition = partition }, progress, cancellationToken),
            PlannedFormat f => operations.FormatPartitionAsync(new FormatPartitionRequest { DiskNumber = fresh.Number, PartitionNumber = target.Number, FileSystem = f.FileSystem, Label = f.Label, QuickFormat = f.QuickFormat, ExpectedDisk = disk, ExpectedPartition = partition }, progress, cancellationToken),
            _ => Task.CompletedTask,
        };
    }
}
