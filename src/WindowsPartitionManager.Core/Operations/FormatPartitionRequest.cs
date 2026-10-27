using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Reformat an existing partition in place. Everything on it is lost.</summary>
public sealed record FormatPartitionRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    /// <summary>"NTFS", "exFAT", "FAT32" or "ReFS".</summary>
    public string FileSystem { get; init; } = "NTFS";

    public string Label { get; init; } = string.Empty;

    public bool QuickFormat { get; init; } = true;

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}

public static class FormatPlanner
{
    public static bool CanFormat(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        return partition.Kind == PartitionKind.Basic && !partition.IsBoot && !partition.IsSystem && !disk.IsReadOnly && !disk.IsOffline;
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, Partition partition, FormatPartitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

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

        if (partition.IsBoot || partition.IsSystem)
        {
            Error(CoreText.Get("Format_WindowsPartition"));
        }
        else if (partition.Kind != PartitionKind.Basic)
        {
            Error(CoreText.Format("Format_OnlyBasicData", CoreText.PartitionType(partition.TypeDescription)));
        }

        if (!PartitionPlanner.SupportedFileSystems.Contains(request.FileSystem, StringComparer.OrdinalIgnoreCase))
        {
            Error(CoreText.Format("Common_UnsupportedFileSystem", request.FileSystem, string.Join(", ", PartitionPlanner.SupportedFileSystems)));
        }
        else if (request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && partition.SizeBytes > PartitionPlanner.Fat32MaximumBytes)
        {
            Error(CoreText.Get("Common_Fat32TooLarge"));
        }

        var labelLimit = request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? 11 : 32;
        if (request.Label.Length > labelLimit)
        {
            Error(CoreText.Format("Common_LabelTooLong", request.FileSystem, labelLimit));
        }

        if (partition.Volume is { } encrypted)
        {
            issues.AddRange(SafetyChecks.Encryption(encrypted, EncryptionUse.Destroy));
        }

        if (partition.Volume is { } volume && volume.UsedBytes > 0)
        {
            Warn(CoreText.Format("Common_VolumeHoldsData", ByteSize.Format(volume.UsedBytes), volume.Label is { Length: > 0 } l ? $" (\"{l}\")" : string.Empty));
        }
        else
        {
            Warn(CoreText.Get("Common_EverythingLost"));
        }

        if (!request.QuickFormat)
        {
            Warn(CoreText.Get("Common_FullFormatSlow"));
        }

        if (request.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase) || request.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
        {
            Warn(CoreText.Format("Format_CannotResizeLater", request.FileSystem));
        }

        issues.AddRange(SafetyChecks.Disk(disk));
        return issues;
    }
}
