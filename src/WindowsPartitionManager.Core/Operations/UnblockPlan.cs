using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;

namespace WindowsPartitionManager.Core.Operations;

public enum UnblockAction
{
    /// <summary>powercfg /h off: deletes hiberfil.sys immediately; also turns Fast Startup off.</summary>
    DisableHibernation,

    /// <summary>Remove the page file from this volume; takes effect after a reboot.</summary>
    DisablePageFile,

    /// <summary>Delete System Restore points / shadow copies on this volume; immediate and not reversible.</summary>
    DeleteShadowCopies,
}

/// <summary>One thing the user can do to let Windows shrink further, with its consequences spelled out.</summary>
public sealed record UnblockStep(
    UnblockAction Action,
    string Title,
    string Description,
    bool RequiresReboot,
    bool Reversible,
    ulong BytesPastTarget,
    IReadOnlyList<string> Files);

/// <summary>Turns the blockers of a <see cref="ShrinkReport"/> into concrete, explained steps.</summary>
public static class UnblockPlanner
{
    public static IReadOnlyList<UnblockStep> Plan(ShrinkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var steps = new List<UnblockStep>();
        var letter = report.DriveLetter;

        AddIfAny(BlockerKind.HibernationFile, UnblockAction.DisableHibernation,
            CoreText.Get("Unblock_HibernationTitle"),
            CoreText.Get("Unblock_HibernationText"),
            requiresReboot: false, reversible: true);

        AddIfAny(BlockerKind.PageFile, UnblockAction.DisablePageFile,
            CoreText.Format("Unblock_PageFileTitle", letter),
            CoreText.Format("Unblock_PageFileText", letter),
            requiresReboot: true, reversible: true);

        AddIfAny(BlockerKind.ShadowCopyStorage, UnblockAction.DeleteShadowCopies,
            CoreText.Format("Unblock_ShadowTitle", letter),
            CoreText.Format("Unblock_ShadowText", letter),
            requiresReboot: false, reversible: false);

        return steps;

        void AddIfAny(BlockerKind kind, UnblockAction action, string title, string description, bool requiresReboot, bool reversible)
        {
            var blockers = report.BlockersPastTarget.Where(b => b.Kind == kind).ToList();
            if (blockers.Count == 0)
            {
                return;
            }

            var bytes = blockers.Aggregate(0UL, (sum, b) => sum + report.Geometry.ClustersToBytes(b.ClustersBeyondTarget));
            steps.Add(new UnblockStep(action, title, description, requiresReboot, reversible, bytes, blockers.Select(b => b.Path).ToList()));
        }
    }

    public static string Summarize(IReadOnlyList<UnblockStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            return CoreText.Get("Unblock_NothingRemovable");
        }

        var reboot = steps.Any(s => s.RequiresReboot) ? CoreText.Get("Unblock_SummaryReboot") : string.Empty;
        return CoreText.Format("Unblock_Summary", steps.Count, ByteSize.Format(steps.Aggregate(0UL, (s, x) => s + x.BytesPastTarget)), reboot);
    }
}
