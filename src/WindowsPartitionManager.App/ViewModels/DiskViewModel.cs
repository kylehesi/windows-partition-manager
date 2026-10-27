using System.Windows.Input;
using System.Windows.Media;
using WindowsPartitionManager.App.Controls;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>One entry of a row's Actions menu.</summary>
public sealed record RowAction(string Name, ICommand Command, bool IsDestructive = false);

/// <summary>A disk state change the user picked from a disk's menu.</summary>
public sealed record DiskStateTarget(Disk Disk, DiskStateChange Change);

/// <summary>One entry of a disk's menu; unlike a row action it carries its own parameter.</summary>
public sealed record DiskAction(string Name, ICommand Command, object Parameter);

public sealed class DiskViewModel
{
    public DiskViewModel(Disk disk, MainViewModel owner)
    {
        Disk = disk;
        Segments = DiskLayout.GetSegments(disk);
        Rows = Segments.Select(s => new SegmentRow(disk, s, owner)).ToList();

        var flags = new List<string>();
        if (disk.IsSystem) flags.Add(Localization.Get("Disk_System"));
        if (disk.IsBoot) flags.Add(Localization.Get("Disk_Boot"));
        if (disk.IsReadOnly) flags.Add(Localization.Get("Disk_ReadOnly"));
        if (disk.IsOffline) flags.Add(Localization.Get("Disk_Offline"));
        if (disk.Health != HealthStatus.Healthy) flags.Add(disk.Health.ToString());

        var style = disk.Style == PartitionStyle.Raw ? Localization.Get("Disk_NotInitialized") : disk.Style.ToString().ToUpperInvariant();
        Title = $"Disk {disk.Number} \u2022 {disk.FriendlyName}";
        Header = $"{Title} ({ByteSize.Format(disk.SizeBytes)}, {style}, {disk.BusType})";
        HasFlags = flags.Count > 0;
        Subtitle = HasFlags ? string.Join(", ", flags) : string.Empty;

        // Health is read-only, so it is offered for every disk, Windows' own included.
        var actions = new List<DiskAction> { new(Localization.Get("DiskAction_Health"), owner.HealthCommand, disk) };
        actions.AddRange(DiskStatePlanner.Available(disk)
            .Select(change => new DiskAction(Localization.Get($"DiskAction_{change}"), owner.DiskStateCommand, new DiskStateTarget(disk, change))));
        if (VirtualDiskPlanner.CanDetach(disk))
        {
            actions.Add(new DiskAction(Localization.Get("DiskAction_Detach"), owner.DetachVirtualDiskCommand, disk));
        }

        Actions = actions;
    }

    /// <summary>What the disk's own menu offers (online/offline, read-only/writable); empty for Windows' disk.</summary>
    public IReadOnlyList<DiskAction> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    /// <summary>Group-box header: "Disk 0 • Model (1.82 TB, GPT, NVMe)".</summary>
    public string Header { get; }

    /// <summary>True when the disk is system/boot, read-only, offline or unhealthy; the flag line is shown only then.</summary>
    public bool HasFlags { get; }

    public Disk Disk { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<DiskSegment> Segments { get; }

    public IReadOnlyList<SegmentRow> Rows { get; }
}

/// <summary>One line of the partition table shown under each disk map.</summary>
public sealed class SegmentRow
{
    public SegmentRow(Disk disk, DiskSegment segment, MainViewModel owner)
    {
        Disk = disk;
        Segment = segment;
        Offset = ByteSize.Format(segment.OffsetBytes);
        Size = ByteSize.Format(segment.SizeBytes);
        Swatch = PartitionColors.AccentFor(segment);

        var actions = new List<RowAction>();

        if (segment.Partition is not { } partition)
        {
            Name = Localization.Get("Common_Unallocated");
            Type = Localization.Get("Segment_FreeSpace");
            // A RAW disk must be initialized before anything can be created on it.
            CanCreate = disk.Style is PartitionStyle.Gpt or PartitionStyle.Mbr && !disk.IsReadOnly && !disk.IsOffline;
            CanInitialize = disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown && !disk.IsReadOnly && !disk.IsOffline;

            if (CanInitialize) actions.Add(new RowAction(Localization.Get("Segment_InitializeGpt"), owner.InitializeCommand));
            if (CanCreate) actions.Add(new RowAction(Localization.Get("Segment_NewPartition"), owner.NewPartitionCommand));
            Actions = actions;
            return;
        }

        Partition = partition;
        PartitionNumber = partition.Number;
        DriveLetter = partition.DriveLetter;
        Name = partition.DriveLetter is { } letter ? $"{letter}:" : Localization.Format("Segment_Partition", partition.Number);
        Type = CoreText.PartitionType(partition.TypeDescription);

        CanResize = ResizePlanner.CanResize(partition) && !disk.IsReadOnly && !disk.IsOffline;
        CanDelete = !PartitionPlanner.ValidateDelete(disk, partition).Any(i => i.IsError);
        CanFormat = FormatPlanner.CanFormat(disk, partition);
        CanEditProperties = DriveLetterPlanner.CanChange(disk, partition) || LabelPlanner.CanRename(disk, partition);
        CanCheck = CheckPlanner.Available(disk, partition).Count > 0;

        if (partition.Volume is { } volume)
        {
            FileSystem = volume.FileSystem;
            Label = volume.Label ?? string.Empty;
            Used = ByteSize.Format(volume.UsedBytes);
            Free = ByteSize.Format(volume.FreeBytes);
            CanAnalyze = partition.DriveLetter is not null &&
                         volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }

        if (CanAnalyze) actions.Add(new RowAction(Localization.Get("Segment_AnalyzeShrink"), owner.AnalyzeCommand));
        if (CanResize) actions.Add(new RowAction(Localization.Get("Segment_Resize"), owner.ResizeCommand));
        if (CanCheck) actions.Add(new RowAction(Localization.Get("Segment_Check"), owner.CheckCommand));
        if (CanEditProperties) actions.Add(new RowAction(Localization.Get("Segment_Properties"), owner.PropertiesCommand));
        if (CanFormat) actions.Add(new RowAction(Localization.Get("Segment_Format"), owner.FormatCommand, IsDestructive: true));
        if (CanDelete) actions.Add(new RowAction(Localization.Get("Segment_Delete"), owner.DeleteCommand, IsDestructive: true));
        Actions = actions;
    }

    public Disk Disk { get; }

    public DiskSegment Segment { get; }

    public int DiskNumber => Disk.Number;

    public Partition? Partition { get; }

    public int? PartitionNumber { get; }

    public char? DriveLetter { get; }

    public bool IsFree => Segment.IsFree;

    /// <summary>Same colour as this row's block in the disk map.</summary>
    public Brush Swatch { get; }

    /// <summary>Everything the user may do with this row, in the order shown in the Actions menu.</summary>
    public IReadOnlyList<RowAction> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    /// <summary>True for NTFS/ReFS data partitions on a writable disk.</summary>
    public bool CanResize { get; }

    /// <summary>True when the delete rules raise no error (never for Windows' own partitions).</summary>
    public bool CanDelete { get; }

    /// <summary>True for basic data partitions that are not the Windows volume.</summary>
    public bool CanFormat { get; }

    /// <summary>True when Windows offers a file system check for the volume.</summary>
    public bool CanCheck { get; }

    /// <summary>True when the drive letter or the volume label can be changed.</summary>
    public bool CanEditProperties { get; }

    /// <summary>True for the free row of a disk that has no partition table yet.</summary>
    public bool CanInitialize { get; }

    /// <summary>True for mounted NTFS volumes with a drive letter: the only thing shrink analysis understands today.</summary>
    public bool CanAnalyze { get; }

    /// <summary>True for unallocated space on an initialized, writable disk.</summary>
    public bool CanCreate { get; }

    public string Name { get; }

    public string Type { get; }

    public string FileSystem { get; } = string.Empty;

    public string Label { get; } = string.Empty;

    public string Offset { get; }

    public string Size { get; }

    public string Used { get; } = string.Empty;

    public string Free { get; } = string.Empty;
}
