using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;

namespace WindowsPartitionManager.Core.Analysis;

/// <summary>
/// Explains why a volume cannot be shrunk to a given size: which files Windows refuses to move,
/// where they sit, and what would unblock them.
/// </summary>
public static class ShrinkAnalyzer
{
    /// <summary>Default target when none is given: the used data plus 1/10 headroom (integer math, no rounding surprises).</summary>
    private const ulong DefaultHeadroomDivisor = 10;

    public static async Task<ShrinkReport> AnalyzeAsync(
        IVolumeInspection volume,
        ulong? targetSizeBytes = null,
        SupportedSize? windowsSupportedSize = null,
        IProgress<InspectProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var geometry = volume.Geometry;
        var bitmap = volume.Bitmap;
        var clusterSize = geometry.BytesPerCluster;

        progress?.Report(new InspectProgress(CoreText.Get("Analysis_ReadingBitmap"), 0, 1));
        var usedClusters = bitmap.UsedCount;
        var lastUsedLcn = bitmap.LastUsedLcn ?? 0;

        var targetClusters = targetSizeBytes is { } explicitTarget
            ? Math.Min(geometry.BytesToClusters(explicitTarget), geometry.TotalClusters)
            : Math.Min(usedClusters + (usedClusters + DefaultHeadroomDivisor - 1) / DefaultHeadroomDivisor, geometry.TotalClusters);
        targetClusters = Math.Max(targetClusters, 1);

        var files = await volume.GetFilesAsync(targetClusters, IsUnmovableByPolicy, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new InspectProgress(CoreText.Get("Analysis_Classifying"), 0, 1));

        var blockers = new List<ShrinkBlocker>();
        var movableFiles = 0;
        ulong movableClustersBeyond = 0;
        ulong attributedClustersBeyond = 0;

        // Limits are global: a blocker anywhere on the volume caps how far it can shrink.
        var mftEnd = geometry.MftStartLcn + geometry.BytesToClusters(geometry.MftValidDataLength);
        var mirrorEnd = geometry.MftMirrorStartLcn + Math.Max(1, geometry.BytesToClusters(4UL * geometry.BytesPerFileRecordSegment));
        var metadataLimit = Math.Max(mftEnd, mirrorEnd);
        var blockerLimit = metadataLimit;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = volume.GetPath(file.FileId);
            var beyond = file.ClustersFrom(targetClusters);
            attributedClustersBeyond += beyond;

            var classification = Classify(path, file);
            if (classification is null)
            {
                if (beyond > 0)
                {
                    movableFiles++;
                    movableClustersBeyond += beyond;
                }

                continue;
            }

            var (kind, reason) = classification.Value;
            var end = file.FurthestEndLcn;
            if (end == 0)
            {
                continue; // resident or empty: occupies no clusters of its own
            }

            var blocker = new ShrinkBlocker(path, kind, reason, end, beyond, file.TotalClusters);
            blockers.Add(blocker);

            blockerLimit = Math.Max(blockerLimit, end);
            if (!blocker.IsRemovable)
            {
                metadataLimit = Math.Max(metadataLimit, end);
            }
        }

        // Whatever gets moved, the data itself still has to fit.
        blockerLimit = Math.Max(blockerLimit, usedClusters);
        metadataLimit = Math.Max(metadataLimit, usedClusters);

        var usedBeyond = bitmap.CountUsed(targetClusters, geometry.TotalClusters);
        var unattributed = usedBeyond > attributedClustersBeyond ? usedBeyond - attributedClustersBeyond : 0;

        var pastTarget = blockers
            .Where(b => b.FurthestEndLcn > targetClusters)
            .OrderByDescending(b => b.FurthestEndLcn)
            .ToList();

        var report = new ShrinkReport
        {
            DriveLetter = volume.DriveLetter,
            Geometry = geometry,
            LayoutStats = volume.LayoutStats,
            UsedBytes = usedClusters * clusterSize,
            TargetSizeBytes = targetClusters * clusterSize,
            TargetWasExplicit = targetSizeBytes.HasValue,
            MinimumSizeWithoutMovingBytes = (lastUsedLcn + 1) * clusterSize,
            WindowsMinimumSizeBytes = windowsSupportedSize?.MinimumBytes,
            MinimumSizeWithBlockersBytes = blockerLimit * clusterSize,
            MinimumSizeMetadataOnlyBytes = metadataLimit * clusterSize,
            BlockersPastTarget = pastTarget,
            BlockersBelowTarget = blockers.Count - pastTarget.Count,
            MovableFilesBeyondTarget = movableFiles,
            MovableBytesBeyondTarget = movableClustersBeyond * clusterSize,
            UnattributedBytesBeyondTarget = unattributed * clusterSize,
            Advice = [],
        };

        return report with { Advice = BuildAdvice(report) };
    }

    /// <summary>
    /// Adds Windows' own shrink limit to a finished report. Querying that limit is slow, so callers
    /// can run it in parallel with the analysis and merge it afterwards.
    /// </summary>
    public static ShrinkReport WithWindowsLimit(ShrinkReport report, SupportedSize? windowsSupportedSize)
    {
        ArgumentNullException.ThrowIfNull(report);

        var updated = report with { WindowsMinimumSizeBytes = windowsSupportedSize?.MinimumBytes };
        return updated with { Advice = BuildAdvice(updated) };
    }

    /// <summary>Paths Windows will not relocate regardless of what the file system flags say.</summary>
    public static bool IsUnmovableByPolicy(string path) => ClassifyByPath(path) is not null;

    /// <summary>Returns null when Windows can relocate the file during a shrink.</summary>
    public static (BlockerKind Kind, string Reason)? Classify(string path, FileExtents file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (ClassifyByPath(path) is { } byPath)
        {
            return byPath;
        }

        if (file.IsImmovable)
        {
            return (BlockerKind.FileSystemImmovable, CoreText.Get("Analysis_ReasonImmovable"));
        }

        return null;
    }

    private static (BlockerKind Kind, string Reason)? ClassifyByPath(string path)
    {
        var separator = path.LastIndexOf('\\');
        var name = separator < 0 ? path : path[(separator + 1)..];
        var isRoot = separator < 0;

        if (isRoot)
        {
            if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            {
                return (BlockerKind.PageFile, CoreText.Get("Analysis_ReasonPageFile"));
            }

            if (name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))
            {
                return (BlockerKind.HibernationFile, CoreText.Get("Analysis_ReasonHibernation"));
            }
        }

        if (name.StartsWith('$') && (isRoot || path.StartsWith("$Extend\\", StringComparison.OrdinalIgnoreCase)))
        {
            return (BlockerKind.NtfsMetadata, CoreText.Get("Analysis_ReasonMetadata"));
        }

        if (path.StartsWith("System Volume Information\\", StringComparison.OrdinalIgnoreCase))
        {
            return (BlockerKind.ShadowCopyStorage, CoreText.Get("Analysis_ReasonShadow"));
        }

        return null;
    }

    private static List<string> BuildAdvice(ShrinkReport report)
    {
        var advice = new List<string>();
        var kinds = report.BlockersPastTarget.Select(b => b.Kind).ToHashSet();

        if (report.WindowsMinimumSizeBytes is { } windowsMin && windowsMin <= report.TargetSizeBytes && report.BlockersPastTarget.Count == 0)
        {
            advice.Add(CoreText.Format("Analysis_AdviceWindowsReaches", ByteSize.Format(windowsMin)));
        }

        if (kinds.Contains(BlockerKind.PageFile))
        {
            advice.Add(CoreText.Get("Analysis_AdvicePageFile"));
        }

        if (kinds.Contains(BlockerKind.HibernationFile))
        {
            advice.Add(CoreText.Get("Analysis_AdviceHibernation"));
        }

        if (kinds.Contains(BlockerKind.ShadowCopyStorage))
        {
            advice.Add(CoreText.Format("Analysis_AdviceShadow", report.DriveLetter));
        }

        if (kinds.Contains(BlockerKind.NtfsMetadata) || kinds.Contains(BlockerKind.FileSystemImmovable))
        {
            advice.Add(CoreText.Format("Analysis_AdviceMetadata", ByteSize.Format(report.MinimumSizeMetadataOnlyBytes)));
        }

        if (report.MovableFilesBeyondTarget > 0)
        {
            advice.Add(CoreText.Format("Analysis_AdviceMovable", report.MovableFilesBeyondTarget, ByteSize.Format(report.MovableBytesBeyondTarget), report.DriveLetter));
        }

        if (report.UnattributedBytesBeyondTarget > 0)
        {
            advice.Add(CoreText.Format("Analysis_AdviceUnattributed", ByteSize.Format(report.UnattributedBytesBeyondTarget)));
        }

        if (advice.Count == 0)
        {
            advice.Add(CoreText.Get("Analysis_AdviceNothing"));
        }

        return advice;
    }
}
