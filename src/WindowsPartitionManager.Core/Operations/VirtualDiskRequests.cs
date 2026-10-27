using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>A new VHDX file, attached as a disk right away.</summary>
public sealed record CreateVirtualDiskRequest
{
    public required string Path { get; init; }

    public required ulong SizeBytes { get; init; }

    /// <summary>The partition table to write after attaching, or <see cref="PartitionStyle.Raw"/> to leave the disk empty.</summary>
    public PartitionStyle Style { get; init; } = PartitionStyle.Gpt;
}

/// <summary>Detach the virtual disk that is attached as <see cref="DiskNumber"/>.</summary>
public sealed record DetachVirtualDiskRequest
{
    public required int DiskNumber { get; init; }

    public DiskFingerprint? ExpectedDisk { get; init; }
}

public static class VirtualDiskPlanner
{
    /// <summary>Small enough to try things out, large enough for NTFS with room for a second partition.</summary>
    public const ulong MinimumSizeBytes = 64UL * 1024 * 1024;

    /// <summary>The VHDX format's own limit.</summary>
    public const ulong MaximumSizeBytes = 64UL * 1024 * 1024 * 1024 * 1024;

    public static bool CanDetach(Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return disk.VirtualDiskPath is not null && !disk.IsSystem && !disk.IsBoot;
    }

    /// <param name="fileExists">Whether something already exists at the path (the planner does not touch the file system itself).</param>
    public static IReadOnlyList<ValidationIssue> ValidateCreate(CreateVirtualDiskRequest request, bool fileExists, ulong? freeBytesOnTarget = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

        if (!request.Path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || !System.IO.Path.IsPathFullyQualified(request.Path))
        {
            Error(CoreText.Get("Vhd_PathNeeded"));
        }
        else if (fileExists)
        {
            Error(CoreText.Format("Vhd_FileExists", request.Path));
        }

        if (request.SizeBytes < MinimumSizeBytes)
        {
            Error(CoreText.Format("Vhd_TooSmall", ByteSize.Format(MinimumSizeBytes)));
        }
        else if (request.SizeBytes > MaximumSizeBytes)
        {
            Error(CoreText.Format("Vhd_TooLarge", ByteSize.Format(MaximumSizeBytes)));
        }

        if (request.Style is not (PartitionStyle.Gpt or PartitionStyle.Mbr or PartitionStyle.Raw))
        {
            Error(CoreText.Get("Common_RequestMismatch"));
        }

        // The file grows as data is written, so a full target drive only shows up later.
        if (freeBytesOnTarget is { } free && free < request.SizeBytes)
        {
            Warn(CoreText.Format("Vhd_MayOutgrowDrive", ByteSize.Format(request.SizeBytes), ByteSize.Format(free)));
        }

        return issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateDetach(Disk disk, DetachVirtualDiskRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        if (request.DiskNumber != disk.Number)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, CoreText.Get("Common_RequestMismatch")));
            return issues;
        }

        if (!CanDetach(disk))
        {
            issues.Add(new ValidationIssue(IssueSeverity.Error, CoreText.Get("Vhd_NotVirtual")));
            return issues;
        }

        var letters = string.Join(", ", disk.Partitions
            .Select(p => p.DriveLetter ?? p.Volume?.DriveLetter)
            .OfType<char>()
            .Select(l => $"{char.ToUpperInvariant(l)}:"));
        if (letters.Length > 0)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, CoreText.Format("DiskState_OfflineHidesDrives", letters)));
        }

        return issues;
    }
}
