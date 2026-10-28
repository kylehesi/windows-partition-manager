using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>A queued step with what its rules said when it was added.</summary>
public sealed record QueuedStep(PlannedOperation Operation, string Description, IReadOnlyList<ValidationIssue> Warnings);

/// <summary>
/// Changes planned on paper. Every step is checked by the same planner rules as an immediate
/// change, but against the layout the earlier steps leave behind. Nothing here touches a disk;
/// <see cref="PlanRunner"/> applies the steps one by one.
/// </summary>
public sealed class OperationPlan
{
    private readonly List<QueuedStep> _steps = [];
    private readonly IReadOnlySet<char> _otherLetters;
    private IReadOnlyList<Disk> _base;
    private IReadOnlyList<Disk> _projected;

    /// <param name="disks">The disks as Windows reports them now.</param>
    /// <param name="otherLetters">Drive letters held by things that are not partitions (optical drives, network drives).</param>
    public OperationPlan(IReadOnlyList<Disk> disks, IReadOnlySet<char>? otherLetters = null)
    {
        ArgumentNullException.ThrowIfNull(disks);
        _base = disks;
        _projected = disks;
        _otherLetters = otherLetters ?? new HashSet<char>();
    }

    public IReadOnlyList<QueuedStep> Steps => _steps;

    public bool IsEmpty => _steps.Count == 0;

    /// <summary>The disks as they will be once every step has run.</summary>
    public IReadOnlyList<Disk> Projected => _projected;

    /// <summary>Checks the step against the projected layout and adds it when no rule reports an error.</summary>
    public IReadOnlyList<ValidationIssue> TryAdd(PlannedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var issues = Validate(_projected, operation);
        if (!issues.Any(i => i.IsError))
        {
            var before = _projected.First(d => d.Number == operation.DiskNumber);
            _steps.Add(new QueuedStep(operation, operation.Describe(before), issues));
            _projected = Replace(_projected, LayoutSimulator.Apply(before, operation));
        }

        return issues;
    }

    /// <summary>
    /// Removes a step. Later steps are checked again against the new projection; those that no
    /// longer fit (for example a format of a partition that will no longer be created) go too.
    /// Returns every step that was removed.
    /// </summary>
    public IReadOnlyList<QueuedStep> RemoveAt(int index)
    {
        var removed = new List<QueuedStep> { _steps[index] };
        var keep = _steps.Where((_, i) => i != index).ToList();
        removed.AddRange(Rebuild(_base, keep));
        return removed;
    }

    /// <summary>
    /// The disks were read again (a refresh, a drive plugged in). The plan is checked against the
    /// new reality; steps that no longer fit are dropped and returned.
    /// </summary>
    public IReadOnlyList<QueuedStep> Rebase(IReadOnlyList<Disk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        _base = disks;
        return Rebuild(disks, [.. _steps]);
    }

    public void Clear()
    {
        _steps.Clear();
        _projected = _base;
    }

    /// <summary>The rules for one step against the layout it will meet; a missing disk or partition is an error.</summary>
    public IReadOnlyList<ValidationIssue> Validate(IReadOnlyList<Disk> disks, PlannedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(operation);

        var disk = disks.FirstOrDefault(d => d.Number == operation.DiskNumber);
        if (disk is null)
        {
            return [new ValidationIssue(IssueSeverity.Error, CoreText.Format("Guard_DiskGone", LayoutGuard.RefreshHint))];
        }

        if (operation is PlannedCreate create)
        {
            var letters = new HashSet<char>(PartitionPlanner.LettersInUse(disks));
            letters.UnionWith(_otherLetters);
            return PartitionPlanner.Validate(disk, create.ToRequest(), letters);
        }

        var offset = operation switch
        {
            PlannedDelete d => d.OffsetBytes,
            PlannedResize r => r.OffsetBytes,
            PlannedFormat f => f.OffsetBytes,
            _ => throw new ArgumentException($"Unknown planned operation {operation.GetType().Name}.", nameof(operation)),
        };
        if (disk.Partitions.FirstOrDefault(p => p.OffsetBytes == offset) is not { } partition)
        {
            return [new ValidationIssue(IssueSeverity.Error, CoreText.Get("Queue_TargetGone"))];
        }

        return operation switch
        {
            PlannedDelete => PartitionPlanner.ValidateDelete(disk, partition),
            PlannedResize r => ResizePlanner.Validate(disk, partition, new ResizePartitionRequest { DiskNumber = disk.Number, PartitionNumber = partition.Number, NewSizeBytes = r.NewSizeBytes }),
            PlannedFormat f => FormatPlanner.Validate(disk, partition, new FormatPartitionRequest { DiskNumber = disk.Number, PartitionNumber = partition.Number, FileSystem = f.FileSystem, Label = f.Label, QuickFormat = f.QuickFormat }),
            _ => [],
        };
    }

    private List<QueuedStep> Rebuild(IReadOnlyList<Disk> start, List<QueuedStep> steps)
    {
        _steps.Clear();
        _projected = start;
        var dropped = new List<QueuedStep>();
        foreach (var step in steps)
        {
            if (TryAdd(step.Operation).Any(i => i.IsError))
            {
                dropped.Add(step);
            }
        }

        return dropped;
    }

    private static List<Disk> Replace(IReadOnlyList<Disk> disks, Disk changed)
        => [.. disks.Select(d => d.Number == changed.Number ? changed : d)];
}
