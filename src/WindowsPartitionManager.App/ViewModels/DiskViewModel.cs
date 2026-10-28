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
/// <param name="Glyph">Segoe Fluent Icons character shown before the name.</param>
/// <param name="Shortcut">The key that does the same in the main window, shown at the end of the menu item.</param>
public sealed record RowAction(string Name, ICommand Command, bool IsDestructive = false, string Glyph = "", string Shortcut = "");

/// <summary>A disk state change the user picked from a disk's menu.</summary>
public sealed record DiskStateTarget(Disk Disk, DiskStateChange Change);

/// <summary>A partition table the user picked for an empty disk.</summary>
public sealed record ConvertTarget(Disk Disk, PartitionStyle Style);

/// <summary>One entry of a disk's menu; unlike a row action it carries its own parameter.</summary>
public sealed record DiskAction(string Name, ICommand Command, object Parameter, string Glyph = "");

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
        Details = $"{ByteSize.Format(disk.SizeBytes)}, {style}, {disk.BusType}";
        Header = $"{Title} ({Details})";
        HasFlags = flags.Count > 0;
        Subtitle = HasFlags ? string.Join(", ", flags) : string.Empty;

        // Health is read-only, so it is offered for every disk, Windows' own included.
        var actions = new List<DiskAction> { new(Localization.Get("DiskAction_Health"), owner.HealthCommand, disk, "\uE95E") };
        actions.AddRange(owner.IsPlanning ? [] : DiskStatePlanner.Available(disk)
            .Select(change => new DiskAction(Localization.Get($"DiskAction_{change}"), owner.DiskStateCommand, new DiskStateTarget(disk, change),
                change is DiskStateChange.Online or DiskStateChange.Offline ? "\uE7E8" : "\uE72E")));
        if (!owner.IsPlanning && ConvertDiskPlanner.Target(disk) is { } convertTo)
        {
            actions.Add(new DiskAction(Localization.Get($"DiskAction_ConvertTo{convertTo}"), owner.ConvertCommand, new ConvertTarget(disk, convertTo), "\uE8AB"));
        }

        if (!owner.IsPlanning && VirtualDiskPlanner.CanDetach(disk))
        {
            actions.Add(new DiskAction(Localization.Get("DiskAction_Detach"), owner.DetachVirtualDiskCommand, disk, "\uE711"));
        }

        Actions = actions;
    }

    /// <summary>What the disk's own menu offers (online/offline, read-only/writable); empty for Windows' disk.</summary>
    public IReadOnlyList<DiskAction> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    /// <summary>Space used by the volumes on the disk, as a share of the whole disk.</summary>
    public double UsedFraction => Disk.SizeBytes == 0 ? 0 : Math.Min(1, Disk.Partitions.Sum(p => (double)(p.Volume?.UsedBytes ?? 0)) / Disk.SizeBytes);

    /// <summary>"1.82 TB · NVMe", the short line under the disk's name in a list.</summary>
    public string ShortInfo => $"{ByteSize.Format(Disk.SizeBytes)} \u00b7 {Disk.BusType}";

    /// <summary>"1.82 TB, GPT, NVMe".</summary>
    public string Details { get; }

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
            CanInitialize = disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown && !disk.IsReadOnly && !disk.IsOffline && !owner.IsPlanning;

            if (CanInitialize) actions.Add(new RowAction(Localization.Get("Segment_InitializeGpt"), owner.InitializeCommand, Glyph: "\uE8F4"));
            if (CanCreate) actions.Add(new RowAction(Localization.Get("Segment_NewPartition"), owner.NewPartitionCommand, Glyph: "\uE710"));
            Actions = actions;
            return;
        }

        Partition = partition;
        PartitionNumber = partition.Number;
        DriveLetter = partition.DriveLetter;
        Name = partition.DriveLetter is { } letter ? $"{letter}:" : Localization.Format("Segment_Partition", partition.Number);
        Type = CoreText.PartitionType(partition.TypeDescription);
        IsPlanned = LayoutSimulator.IsPlanned(partition);
        if (IsPlanned)
        {
            Type = Localization.Format("Queue_PlannedType", Type);
        }

        CanResize = ResizePlanner.CanResize(partition) && !disk.IsReadOnly && !disk.IsOffline;
        CanDelete = !PartitionPlanner.ValidateDelete(disk, partition).Any(i => i.IsError);
        CanFormat = FormatPlanner.CanFormat(disk, partition);
        // These act on the disk right away, so they wait until the queue has been applied or discarded.
        var immediate = !owner.IsPlanning;
        CanEditProperties = immediate && (DriveLetterPlanner.CanChange(disk, partition) || LabelPlanner.CanRename(disk, partition));
        CanCheck = immediate && CheckPlanner.Available(disk, partition).Count > 0;
        CanOptimize = immediate && OptimizePlanner.Available(disk, partition).Count > 0;

        if (partition.Volume is { } volume)
        {
            FileSystem = volume.FileSystem;
            Label = volume.Label ?? string.Empty;
            Used = ByteSize.Format(volume.UsedBytes);
            Free = ByteSize.Format(volume.FreeBytes);
            UsedFraction = volume.UsedFraction;
            UsageText = Localization.Format("Segment_Usage", Used, ByteSize.Format(volume.SizeBytes));
            UsageShort = $"{Used} / {ByteSize.Format(volume.SizeBytes)}";
            CanAnalyze = immediate && partition.DriveLetter is not null &&
                         volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            EncryptionText = DescribeEncryption(volume.BitLocker);
        }

        if (CanAnalyze) actions.Add(new RowAction(Localization.Get("Segment_Space"), owner.SpaceCommand, Glyph: "\uEB05"));
        if (CanAnalyze) actions.Add(new RowAction(Localization.Get("Segment_AnalyzeShrink"), owner.AnalyzeCommand, Glyph: "\uE9D9"));
        if (CanResize) actions.Add(new RowAction(Localization.Get("Segment_Resize"), owner.ResizeCommand, Glyph: "\uE740"));
        if (CanCheck) actions.Add(new RowAction(Localization.Get("Segment_Check"), owner.CheckCommand, Glyph: "\uE9F5"));
        if (CanOptimize) actions.Add(new RowAction(Localization.Get("Segment_Optimize"), owner.OptimizeCommand, Glyph: "\uE945"));
        if (CanEditProperties) actions.Add(new RowAction(Localization.Get("Segment_Properties"), owner.PropertiesCommand, Glyph: "\uE8EC", Shortcut: "F2"));
        if (CanFormat) actions.Add(new RowAction(Localization.Get("Segment_Format"), owner.FormatCommand, IsDestructive: true, Glyph: "\uE74E"));
        if (CanDelete) actions.Add(new RowAction(Localization.Get("Segment_Delete"), owner.DeleteCommand, IsDestructive: true, Glyph: "\uE74D", Shortcut: "Del"));
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

    /// <summary>True when Windows can TRIM or defragment the volume.</summary>
    public bool CanOptimize { get; }

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

    /// <summary>BitLocker in plain words for the lock icon's tooltip; empty when the volume is not encrypted.</summary>
    public string EncryptionText { get; } = string.Empty;

    public bool IsEncrypted => EncryptionText.Length > 0;

    /// <summary>A partition the queue will create; it does not exist yet.</summary>
    public bool IsPlanned { get; }

    /// <summary>Shared with the disk map tooltip.</summary>
    public static string DescribeEncryption(BitLockerStatus? bitLocker) => bitLocker switch
    {
        { Locked: true } => Localization.Get("BitLocker_StateLocked"),
        { IsConverting: true } b => Localization.Format($"BitLocker_State{b.State}", b.PercentEncrypted?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "?"),
        { IsEncrypted: true, ProtectionOn: true } => Localization.Get("BitLocker_StateOn"),
        { IsEncrypted: true } => Localization.Get("BitLocker_StateProtectionOff"),
        _ => string.Empty,
    };

    public string Type { get; }

    public string FileSystem { get; } = string.Empty;

    public string Label { get; } = string.Empty;

    public string Offset { get; }

    public string Size { get; }

    public string Used { get; } = string.Empty;

    /// <summary>Share of the volume in use, 0..1; 0 without a volume.</summary>
    public double UsedFraction { get; }

    /// <summary>"447 GB of 1.67 TB used", or empty without a volume.</summary>
    public string UsageText { get; } = string.Empty;

    /// <summary>"447 GB / 1.67 TB", for places with little room.</summary>
    public string UsageShort { get; } = string.Empty;

    public bool HasUsage => UsageText.Length > 0;

    /// <summary>"Basic data \u00b7 NTFS \u00b7 Data": type, file system and label, whichever are known.</summary>
    public string Summary => string.Join(" \u00b7 ", new[] { Type, FileSystem, Label }.Where(s => s.Length > 0));

    /// <summary>Name/value lines for a details panel; empty values are left out.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Details =>
        new KeyValuePair<string, string>[]
        {
            new(Localization.Get("Col_Type"), Type),
            new(Localization.Get("Col_FileSystem"), FileSystem),
            new(Localization.Get("Col_Label"), Label),
            new(Localization.Get("Col_Size"), Size),
            new(Localization.Get("Col_Offset"), Offset),
            new(Localization.Get("Col_Used"), Used),
            new(Localization.Get("Col_Free"), Free),
            new("BitLocker", EncryptionText),
        }.Where(d => d.Value.Length > 0).ToList();

    public string Free { get; } = string.Empty;
}
