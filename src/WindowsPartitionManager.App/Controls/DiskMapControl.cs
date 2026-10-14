using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.App.Controls;

/// <summary>
/// Draws a disk as a horizontal bar of partitions and free regions. Widths follow the sizes,
/// but every segment is at least as wide as its own label, so small partitions such as EFI,
/// MSR and Recovery stay readable; the large ones give up the difference. Rendered directly with
/// a DrawingContext, so a disk with dozens of partitions costs nothing.
/// </summary>
public sealed class DiskMapControl : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<DiskSegment>),
        typeof(DiskMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Gap = 2;
    private const double TextInset = 6;
    private const double TitleSize = 12;
    private const double DetailSize = 11;
    private const double BarHeight = 56;

    private static readonly Brush TextBrush = CreateTextBrush();
    private static readonly Typeface TitleTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface DetailTypeface = new("Segoe UI");

    private double[] _lastWidths = [];

    public IReadOnlyList<DiskSegment>? Segments
    {
        get => (IReadOnlyList<DiskSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>The two lines written inside a segment.</summary>
    public static (string Title, string Detail) LabelFor(DiskSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var size = ByteSize.Format(segment.SizeBytes);
        if (segment.Partition is not { } partition)
        {
            return (Localization.Get("Common_Unallocated"), size);
        }

        var title = partition.DriveLetter is { } letter
            ? (partition.Volume?.Label is { Length: > 0 } label ? $"{letter}: {label}" : $"{letter}:")
            : partition.TypeDescription;
        return (title, size);
    }

    /// <summary>
    /// Asks for a modest width only; the layout stretches the map across whatever the table needs.
    /// Asking for all available width would make a window that sizes itself to its content as
    /// wide as the screen.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
        => new(Math.Min(400, availableSize.Width), BarHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var segments = Segments;
        var width = ActualWidth;
        var height = ActualHeight;
        if (segments is null || segments.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labels = segments.Select(LabelFor).ToArray();
        var minimums = labels
            .Select(l => Math.Max(Measure(l.Title, TitleTypeface, TitleSize, pixelsPerDip), Measure(l.Detail, DetailTypeface, DetailSize, pixelsPerDip)) + 2 * TextInset + Gap)
            .ToArray();
        var widths = ComputeWidths(segments, minimums, width);
        _lastWidths = widths;

        double x = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var rect = new Rect(x + Gap / 2, 0.5, Math.Max(0, widths[i] - Gap), height - 1);

            if (segment.Partition is not { } partition)
            {
                dc.DrawRectangle(PartitionColors.FreeFill, PartitionColors.FreeBorder, rect);
            }
            else
            {
                var (fill, accent) = PartitionColors.For(partition.Kind);
                dc.DrawRectangle(fill, PartitionColors.PartitionBorder, rect);

                if (partition.Volume is { } volume && volume.UsedFraction > 0)
                {
                    var usedRect = new Rect(rect.X, rect.Y, rect.Width * Math.Min(1, volume.UsedFraction), rect.Height);
                    dc.DrawRectangle(accent, null, usedRect);
                }
            }

            DrawLabel(dc, rect, labels[i].Title, labels[i].Detail, pixelsPerDip);
            x += widths[i];
        }
    }

    /// <summary>On a very small window a label may still be trimmed, so the hovered segment is described in a tooltip.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var segments = Segments;
        if (segments is null || _lastWidths.Length != segments.Count)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        double start = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            if (x >= start && x < start + _lastWidths[i])
            {
                var segment = segments[i];
                var text = segment.Partition is { } p
                    ? $"{(p.DriveLetter is { } l ? $"{l}: " : string.Empty)}{p.TypeDescription}" +
                      (p.Volume is { } v ? $" ({v.FileSystem}{(v.Label is { Length: > 0 } label ? $" \"{label}\"" : string.Empty)})" : string.Empty) +
                      $"\n{ByteSize.Format(segment.SizeBytes)} at {ByteSize.Format(segment.OffsetBytes)}" +
                      (p.Volume is { } vol ? $"\n{ByteSize.Format(vol.UsedBytes)} used, {ByteSize.Format(vol.FreeBytes)} free" : string.Empty)
                    : $"{Localization.Get("Common_Unallocated")}\n{ByteSize.Format(segment.SizeBytes)} at {ByteSize.Format(segment.OffsetBytes)}";

                if (!Equals(ToolTip, text))
                {
                    ToolTip = text;
                }

                return;
            }

            start += _lastWidths[i];
        }
    }

    /// <summary>
    /// Proportional widths, raised to each segment's label width; the excess is taken from the
    /// segments that have room to spare, in proportion to that room. When even the labels do not
    /// fit, every segment is scaled down equally and the text is trimmed with an ellipsis.
    /// </summary>
    internal static double[] ComputeWidths(IReadOnlyList<DiskSegment> segments, double[] minimums, double totalWidth)
    {
        var count = segments.Count;
        var minimumTotal = minimums.Sum();
        if (minimumTotal >= totalWidth)
        {
            var scale = totalWidth / minimumTotal;
            return minimums.Select(m => m * scale).ToArray();
        }

        double totalBytes = segments.Sum(s => (double)s.SizeBytes);
        var widths = new double[count];
        for (var i = 0; i < count; i++)
        {
            var proportional = totalBytes <= 0 ? totalWidth / count : totalWidth * segments[i].SizeBytes / totalBytes;
            widths[i] = Math.Max(minimums[i], proportional);
        }

        for (var pass = 0; pass < 8; pass++)
        {
            var excess = widths.Sum() - totalWidth;
            if (excess <= 0.5)
            {
                break;
            }

            double slack = 0;
            for (var i = 0; i < count; i++)
            {
                slack += widths[i] - minimums[i];
            }

            if (slack <= 0)
            {
                break;
            }

            for (var i = 0; i < count; i++)
            {
                var room = widths[i] - minimums[i];
                if (room > 0)
                {
                    widths[i] = Math.Max(minimums[i], widths[i] - (excess * room / slack));
                }
            }
        }

        return widths;
    }

    private static SolidColorBrush CreateTextBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
        brush.Freeze();
        return brush;
    }

    private static double Measure(string text, Typeface typeface, double size, double pixelsPerDip)
        => new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, TextBrush, pixelsPerDip).WidthIncludingTrailingWhitespace;

    private static void DrawLabel(DrawingContext dc, Rect rect, string title, string detail, double pixelsPerDip)
    {
        if (rect.Width < (2 * TextInset) + 4)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(rect));

        var maxWidth = Math.Max(1, rect.Width - (2 * TextInset));
        var titleText = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TitleTypeface, TitleSize, TextBrush, pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var detailText = new FormattedText(detail, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, DetailTypeface, DetailSize, TextBrush, pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        dc.DrawText(titleText, new Point(rect.X + TextInset, rect.Y + 8));
        dc.DrawText(detailText, new Point(rect.X + TextInset, rect.Y + rect.Height - 22));
        dc.Pop();
    }
}
