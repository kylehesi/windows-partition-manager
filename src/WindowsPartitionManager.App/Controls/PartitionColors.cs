using System.Windows;
using System.Windows.Media;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.App.Controls;

/// <summary>
/// One colour scheme for both the disk map and the partition table, so a row's swatch matches
/// its block in the map. Fill is the light background, Accent the strong colour (used space,
/// swatches).
/// </summary>
public static class PartitionColors
{
    public static readonly Brush FreeFill = Solid(0xDF, 0xF6, 0xDD);
    public static readonly Brush FreeAccent = Solid(0x2E, 0xA0, 0x43);
    public static readonly Pen FreeBorder = Freeze(new Pen(FreeAccent, 1.5) { DashStyle = DashStyles.Dash });
    public static readonly Pen PartitionBorder = Freeze(new Pen(Solid(0x9A, 0xA4, 0xB2), 1));
    public static readonly Pen PlannedBorder = Freeze(new Pen(Solid(0x2F, 0x80, 0xED), 1.5) { DashStyle = DashStyles.Dash });

    private static readonly (Brush Fill, Brush Accent) Basic = (Solid(0xC9, 0xE2, 0xFB), Solid(0x2F, 0x80, 0xED));
    private static readonly (Brush Fill, Brush Accent) Efi = (Solid(0xFF, 0xE8, 0xC7), Solid(0xF3, 0x9C, 0x12));
    private static readonly (Brush Fill, Brush Accent) Reserved = (Solid(0xE9, 0xDD, 0xF7), Solid(0x8E, 0x44, 0xAD));
    private static readonly (Brush Fill, Brush Accent) Recovery = (Solid(0xCF, 0xF3, 0xEE), Solid(0x16, 0xA0, 0x85));
    private static readonly (Brush Fill, Brush Accent) Other = (Solid(0xFD, 0xE2, 0xE2), Solid(0xE7, 0x4C, 0x3C));

    public static (Brush Fill, Brush Accent) For(PartitionKind kind) => kind switch
    {
        PartitionKind.Basic => Basic,
        PartitionKind.EfiSystem => Efi,
        PartitionKind.MicrosoftReserved or PartitionKind.Ldm or PartitionKind.StorageSpaces => Reserved,
        PartitionKind.Recovery => Recovery,
        _ => Other,
    };

    /// <summary>The strong colour of a map segment: used for the table's row swatches.</summary>
    public static Brush AccentFor(DiskSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.Partition is { } partition ? For(partition.Kind).Accent : FreeAccent;
    }

    private static SolidColorBrush Solid(byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromRgb(r, g, b)));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
