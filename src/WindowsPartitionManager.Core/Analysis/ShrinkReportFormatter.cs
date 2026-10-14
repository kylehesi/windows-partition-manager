using System.Globalization;
using System.Text;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;

namespace WindowsPartitionManager.Core.Analysis;

/// <summary>Plain-text rendering of a <see cref="ShrinkReport"/>, shared by the CLI and the UI.</summary>
public static class ShrinkReportFormatter
{
    public static string ToText(ShrinkReport report, int maxBlockers = 25)
    {
        ArgumentNullException.ThrowIfNull(report);

        var g = report.Geometry;
        var volumeSize = g.VolumeSizeBytes;
        var sb = new StringBuilder();

        sb.AppendLine(CoreText.Format("Report_Title", report.DriveLetter));
        sb.AppendLine(CoreText.Format("Report_VolumeSize", ByteSize.Format(volumeSize), g.TotalClusters, g.BytesPerCluster));
        sb.AppendLine(CoreText.Format("Report_Used", ByteSize.Format(report.UsedBytes)));
        sb.AppendLine(CoreText.Format("Report_Mft", ByteSize.Format(g.ClustersToBytes(g.MftStartLcn)), ByteSize.Format(g.MftValidDataLength)));
        if (report.LayoutStats is { } stats)
        {
            var accounted = g.ClustersToBytes(stats.AllocatedClusters);
            sb.AppendLine(CoreText.Format("Report_FilesScanned", stats.FileCount, stats.ExtentCount, ByteSize.Format(accounted), stats.Elapsed.TotalSeconds));
        }

        sb.AppendLine();

        sb.AppendLine(CoreText.Format("Report_TargetSize", ByteSize.Format(report.TargetSizeBytes), CoreText.Get(report.TargetWasExplicit ? "Report_TargetRequested" : "Report_TargetDefault")));
        sb.AppendLine(CoreText.Format("Report_ShrinkableBy", Shrink(volumeSize, report.TargetSizeBytes)));
        sb.AppendLine();

        sb.AppendLine(CoreText.Get("Report_HowFar"));
        sb.AppendLine(CoreText.Format("Report_WithoutMoving", ByteSize.Format(report.MinimumSizeWithoutMovingBytes), Shrink(volumeSize, report.MinimumSizeWithoutMovingBytes)));
        if (report.WindowsMinimumSizeBytes is { } win)
        {
            sb.AppendLine(CoreText.Format("Report_WindowsDiskManagement", ByteSize.Format(win), Shrink(volumeSize, win)));
        }

        sb.AppendLine(CoreText.Format("Report_AfterMovable", ByteSize.Format(report.MinimumSizeWithBlockersBytes), Shrink(volumeSize, report.MinimumSizeWithBlockersBytes)));
        sb.AppendLine(CoreText.Format("Report_AfterBlockers", ByteSize.Format(report.MinimumSizeMetadataOnlyBytes), Shrink(volumeSize, report.MinimumSizeMetadataOnlyBytes)));
        sb.AppendLine();

        if (report.BlockersPastTarget.Count == 0)
        {
            sb.AppendLine(CoreText.Get("Report_NoBlockers"));
        }
        else
        {
            sb.AppendLine(CoreText.Format("Report_Blockers", report.BlockersPastTarget.Count));
            foreach (var blocker in report.BlockersPastTarget.Take(maxBlockers))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  [{blocker.Kind}] {blocker.Path}");
                sb.AppendLine(CoreText.Format("Report_BlockerEnds", ByteSize.Format(g.ClustersToBytes(blocker.FurthestEndLcn)), ByteSize.Format(g.ClustersToBytes(blocker.ClustersBeyondTarget)), ByteSize.Format(g.ClustersToBytes(blocker.TotalClusters))));
                sb.AppendLine(CultureInfo.InvariantCulture, $"      {blocker.Reason}");
            }

            if (report.BlockersPastTarget.Count > maxBlockers)
            {
                sb.AppendLine(CoreText.Format("Report_More", report.BlockersPastTarget.Count - maxBlockers));
            }
        }

        if (report.BlockersBelowTarget > 0)
        {
            sb.AppendLine(CoreText.Format("Report_BlockersBelow", report.BlockersBelowTarget));
        }

        sb.AppendLine();
        sb.AppendLine(CoreText.Format("Report_Movable", report.MovableFilesBeyondTarget, ByteSize.Format(report.MovableBytesBeyondTarget)));
        if (report.UnattributedBytesBeyondTarget > 0)
        {
            sb.AppendLine(CoreText.Format("Report_Unattributed", ByteSize.Format(report.UnattributedBytesBeyondTarget)));
        }

        sb.AppendLine();
        sb.AppendLine(CoreText.Get("Report_WhatToDo"));
        foreach (var line in report.Advice)
        {
            sb.AppendLine("  - " + line);
        }

        return sb.ToString();
    }

    private static string Shrink(ulong volumeSize, ulong newSize)
        => ByteSize.Format(volumeSize > newSize ? volumeSize - newSize : 0);
}
