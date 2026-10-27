using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>What to do with a whole disk. Each change is undone by its opposite.</summary>
public enum DiskStateChange
{
    /// <summary>Mount the disk's volumes again (Set-Disk -IsOffline $false).</summary>
    Online,

    /// <summary>Dismount every volume on the disk; Windows keeps it offline until it is brought online.</summary>
    Offline,

    /// <summary>Refuse every write to the disk (Set-Disk -IsReadOnly $true).</summary>
    ReadOnly,

    /// <summary>Allow writes again.</summary>
    Writable,
}

public sealed record SetDiskStateRequest
{
    public required int DiskNumber { get; init; }

    public required DiskStateChange Change { get; init; }

    public DiskFingerprint? ExpectedDisk { get; init; }
}

public static class DiskStatePlanner
{
    /// <summary>The changes that make sense for the disk as it is now; Windows' own disk gets none.</summary>
    public static IReadOnlyList<DiskStateChange> Available(Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.IsSystem || disk.IsBoot)
        {
            return [];
        }

        return
        [
            disk.IsOffline ? DiskStateChange.Online : DiskStateChange.Offline,
            disk.IsReadOnly ? DiskStateChange.Writable : DiskStateChange.ReadOnly,
        ];
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, SetDiskStateRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<ValidationIssue>();
        void Error(string message) => issues.Add(new ValidationIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new ValidationIssue(IssueSeverity.Warning, message));

        if (request.DiskNumber != disk.Number)
        {
            Error(CoreText.Get("Common_RequestMismatch"));
            return issues;
        }

        if (disk.IsSystem || disk.IsBoot)
        {
            Error(CoreText.Get("DiskState_WindowsDisk"));
            return issues;
        }

        var letters = string.Join(", ", disk.Partitions
            .Select(p => p.DriveLetter ?? p.Volume?.DriveLetter)
            .OfType<char>()
            .Select(l => $"{char.ToUpperInvariant(l)}:"));

        switch (request.Change)
        {
            case DiskStateChange.Online when !disk.IsOffline:
                Error(CoreText.Get("DiskState_AlreadyOnline"));
                break;
            case DiskStateChange.Offline when disk.IsOffline:
                Error(CoreText.Get("DiskState_AlreadyOffline"));
                break;
            case DiskStateChange.Offline:
                Warn(letters.Length > 0
                    ? CoreText.Format("DiskState_OfflineHidesDrives", letters)
                    : CoreText.Get("DiskState_OfflineHidesDisk"));
                break;
            case DiskStateChange.ReadOnly when disk.IsReadOnly:
                Error(CoreText.Get("DiskState_AlreadyReadOnly"));
                break;
            case DiskStateChange.ReadOnly:
                Warn(CoreText.Get("DiskState_ReadOnlyWarning"));
                break;
            case DiskStateChange.Writable when !disk.IsReadOnly:
                Error(CoreText.Get("DiskState_AlreadyWritable"));
                break;
        }

        return issues;
    }
}
