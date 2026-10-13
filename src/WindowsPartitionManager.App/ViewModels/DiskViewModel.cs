using System.Windows.Input;
using System.Windows.Media;
using WindowsPartitionManager.App.Controls;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>One entry of a row's Actions menu.</summary>
public sealed record RowAction(string Name, ICommand Command, bool IsDestructive = false);

public sealed class DiskViewModel
{
    public DiskViewModel(Disk disk, MainViewModel owner)
    {
        Disk = disk;
        Segments = DiskLayout.GetSegments(disk);
        Rows = Segments.Select(s => new SegmentRow(disk, s, owner)).ToList();

        var flags = new List<string>();
<<<<<<< HEAD
        if (disk.IsSystem) flags.Add(Localization.Get("Disk_System"));
        if (disk.IsBoot) flags.Add(Localization.Get("Disk_Boot"));
        if (disk.IsReadOnly) flags.Add(Localization.Get("Disk_ReadOnly"));
        if (disk.IsOffline) flags.Add(Localization.Get("Disk_Offline"));
        if (disk.Health != HealthStatus.Healthy) flags.Add(disk.Health.ToString());

        var style = disk.Style == PartitionStyle.Raw ? Localization.Get("Disk_NotInitialized") : disk.Style.ToString().ToUpperInvariant();
=======
        if (disk.IsSystem) flags.Add("System");
        if (disk.IsBoot) flags.Add("Boot");
        if (disk.IsReadOnly) flags.Add("Read-only");
        if (disk.IsOffline) flags.Add("Offline");
        if (disk.Health != HealthStatus.Healthy) flags.Add(disk.Health.ToString());

        var style = disk.Style == PartitionStyle.Raw ? "not initialized" : disk.Style.ToString().ToUpperInvariant();
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
        Title = $"Disk {disk.Number} \u2022 {disk.FriendlyName}";
        Header = $"{Title} ({ByteSize.Format(disk.SizeBytes)}, {style}, {disk.BusType})";
        HasFlags = flags.Count > 0;
        Subtitle = HasFlags ? string.Join(", ", flags) : string.Empty;
    }

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
<<<<<<< HEAD
            Name = Localization.Get("Common_Unallocated");
            Type = Localization.Get("Segment_FreeSpace");
=======
            Name = "Unallocated";
            Type = "Free space";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
            // A RAW disk must be initialized before anything can be created on it.
            CanCreate = disk.Style is PartitionStyle.Gpt or PartitionStyle.Mbr && !disk.IsReadOnly && !disk.IsOffline;
            CanInitialize = disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown && !disk.IsReadOnly && !disk.IsOffline;

<<<<<<< HEAD
            if (CanInitialize) actions.Add(new RowAction(Localization.Get("Segment_InitializeGpt"), owner.InitializeCommand));
            if (CanCreate) actions.Add(new RowAction(Localization.Get("Segment_NewPartition"), owner.NewPartitionCommand));
=======
            if (CanInitialize) actions.Add(new RowAction("Initialize disk (GPT)", owner.InitializeCommand));
            if (CanCreate) actions.Add(new RowAction("New partition…", owner.NewPartitionCommand));
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
            Actions = actions;
            return;
        }

        Partition = partition;
        PartitionNumber = partition.Number;
        DriveLetter = partition.DriveLetter;
<<<<<<< HEAD
        Name = partition.DriveLetter is { } letter ? $"{letter}:" : Localization.Format("Segment_Partition", partition.Number);
=======
        Name = partition.DriveLetter is { } letter ? $"{letter}:" : $"Partition {partition.Number}";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
        Type = partition.TypeDescription;

        CanResize = ResizePlanner.CanResize(partition) && !disk.IsReadOnly && !disk.IsOffline;
        CanDelete = !PartitionPlanner.ValidateDelete(disk, partition).Any(i => i.IsError);
        CanFormat = FormatPlanner.CanFormat(disk, partition);

        if (partition.Volume is { } volume)
        {
            FileSystem = volume.FileSystem;
            Label = volume.Label ?? string.Empty;
            Used = ByteSize.Format(volume.UsedBytes);
            Free = ByteSize.Format(volume.FreeBytes);
            CanAnalyze = partition.DriveLetter is not null &&
                         volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }

<<<<<<< HEAD
        if (CanAnalyze) actions.Add(new RowAction(Localization.Get("Segment_AnalyzeShrink"), owner.AnalyzeCommand));
        if (CanResize) actions.Add(new RowAction(Localization.Get("Segment_Resize"), owner.ResizeCommand));
        if (CanFormat) actions.Add(new RowAction(Localization.Get("Segment_Format"), owner.FormatCommand, IsDestructive: true));
        if (CanDelete) actions.Add(new RowAction(Localization.Get("Segment_Delete"), owner.DeleteCommand, IsDestructive: true));
=======
        if (CanAnalyze) actions.Add(new RowAction("Analyze shrink…", owner.AnalyzeCommand));
        if (CanResize) actions.Add(new RowAction("Resize…", owner.ResizeCommand));
        if (CanFormat) actions.Add(new RowAction("Format…", owner.FormatCommand, IsDestructive: true));
        if (CanDelete) actions.Add(new RowAction("Delete…", owner.DeleteCommand, IsDestructive: true));
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
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
