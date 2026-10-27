using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>Give a partition another drive letter, or take its letter away. The data is not touched.</summary>
public sealed record ChangeDriveLetterRequest
{
    public required int DiskNumber { get; init; }

    public required int PartitionNumber { get; init; }

    /// <summary>The new letter, or null to remove the current one.</summary>
    public char? NewDriveLetter { get; init; }

    public DiskFingerprint? ExpectedDisk { get; init; }

    public PartitionFingerprint? ExpectedPartition { get; init; }
}

public static class DriveLetterPlanner
{
    /// <summary>Partitions whose letter the user may change: mounted data volumes that do not hold Windows.</summary>
    public static bool CanChange(Disk disk, Partition partition)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(partition);
        return partition.Kind == PartitionKind.Basic && partition.Volume is not null
               && !partition.IsBoot && !partition.IsSystem && !disk.IsReadOnly && !disk.IsOffline;
    }

    public static IReadOnlyList<ValidationIssue> Validate(Disk disk, Partition partition, ChangeDriveLetterRequest request, IReadOnlySet<char>? lettersInUse = null)
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
            Error(CoreText.Get("Letter_WindowsPartition"));
        }
        else if (partition.Kind != PartitionKind.Basic || partition.Volume is null)
        {
            Error(CoreText.Get("Letter_NoFileSystem"));
        }

        var current = partition.DriveLetter ?? partition.Volume?.DriveLetter;
        if (request.NewDriveLetter is { } letter)
        {
            var upper = char.ToUpperInvariant(letter);
            if (upper < 'C' || upper > 'Z')
            {
                Error(CoreText.Get("Create_LetterRange"));
            }
            else if (current is { } c && char.ToUpperInvariant(c) == upper)
            {
                Error(CoreText.Format("Letter_AlreadyHas", upper));
            }
            else if (lettersInUse is not null && lettersInUse.Contains(upper))
            {
                Error(CoreText.Format("Create_LetterInUse", upper));
            }

            if (current is { } old)
            {
                Warn(CoreText.Format("Letter_ProgramsMayBreak", char.ToUpperInvariant(old)));
            }
        }
        else if (current is null)
        {
            Error(CoreText.Get("Letter_NothingToRemove"));
        }
        else
        {
            Warn(CoreText.Format("Letter_RemoveHides", char.ToUpperInvariant(current.Value)));
            Warn(CoreText.Format("Letter_ProgramsMayBreak", char.ToUpperInvariant(current.Value)));
        }

        issues.AddRange(SafetyChecks.Disk(disk));
        return issues;
    }
}
