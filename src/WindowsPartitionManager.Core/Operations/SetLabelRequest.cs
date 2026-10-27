using System.Buffers;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Rename a volume (its file system label). Nothing else changes.</summary>
public sealed record SetLabelRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    /// <summary>The new label; empty clears it.</summary>
    public string Label { get; init; } = string.Empty;

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}

public static class LabelPlanner
{
    /// <summary>Characters FAT and exFAT labels cannot hold.</summary>
    private const string FatForbidden = "*?.,;:/\\|+=<>[]\"";

    private static readonly SearchValues<char> FatForbiddenValues = SearchValues.Create(FatForbidden);

    public static bool CanRename(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        return partition.Volume is { FileSystem.Length: > 0 } && !disk.IsReadOnly && !disk.IsOffline;
    }

    /// <summary>Longest label the file system accepts.</summary>
    public static int MaximumLength(string fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return IsFat(fileSystem) ? 11 : fileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase) ? 15 : 32;
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, Partition partition, SetLabelRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));

        if (request.DiskNumber != disk.Number || request.PartitionNumber != partition.Number)
        {
            Error(CoreText.Get("Common_RequestMismatch"));
            return issues;
        }

        if (disk.IsReadOnly)
        {
            Error(CoreText.Get("Common_DiskReadOnly"));
        }

        if (disk.IsOffline)
        {
            Error(CoreText.Get("Common_DiskOffline"));
        }

        if (partition.Volume is not { FileSystem.Length: > 0 } volume)
        {
            Error(CoreText.Get("Letter_NoFileSystem"));
            return issues;
        }

        var label = request.Label;
        var limit = MaximumLength(volume.FileSystem);
        if (label.Length > limit)
        {
            Error(CoreText.Format("Common_LabelTooLong", volume.FileSystem, limit));
        }

        var fatFamily = IsFat(volume.FileSystem) || volume.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
        if (fatFamily && label.AsSpan().ContainsAny(FatForbiddenValues))
        {
            Error(CoreText.Format("Label_ForbiddenCharacters", volume.FileSystem, string.Join(" ", FatForbidden.ToCharArray())));
        }

        if (string.Equals(label, volume.Label ?? string.Empty, StringComparison.Ordinal))
        {
            Error(CoreText.Get("Label_Unchanged"));
        }

        return issues;
    }

    private static bool IsFat(string fileSystem)
        => fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("FAT", StringComparison.OrdinalIgnoreCase);
}
