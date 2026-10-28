using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.App;

/// <summary>
/// The default main window: a disk list, the selected disk with its map and partition cards, and a
/// details panel. Selection lives in the view model (SelectedDisk, SelectedRow), so the map, the
/// cards and the details panel always agree.
/// </summary>
public partial class ModernMainWindow : Window
{
    public ModernMainWindow()
    {
        InitializeComponent();
        Controls.DeviceChangeRefresher.Attach(this);
    }

    /// <summary>Opens a button's menu below it on a normal left click.</summary>
    private void Actions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>Clicking a block in the map selects its card.</summary>
    private void Map_SegmentClicked(object? sender, DiskSegment segment)
    {
        if (DataContext is MainViewModel { SelectedDisk: { } disk } viewModel
            && disk.Rows.FirstOrDefault(r => ReferenceEquals(r.Segment, segment)) is { } row)
        {
            viewModel.SelectedRow = row;
        }
    }
}
