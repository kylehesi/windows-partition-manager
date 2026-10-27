using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsPartitionManager.App.ViewModels;

namespace WindowsPartitionManager.App;

public partial class MainWindow : Window
{
    /// <summary>The disk list that currently holds the selection; there is one list per disk.</summary>
    private ListView? _activeList;

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    /// <summary>Plugging in a drive raises a burst of notifications; read the disks once it has settled.</summary>
    private readonly DispatcherTimer _deviceSettle = new() { Interval = TimeSpan.FromSeconds(1.5) };

    /// <summary>A device changed while a dialog was open; refresh when the main window is active again.</summary>
    private bool _refreshPending;

    public MainWindow()
    {
        InitializeComponent();
        _deviceSettle.Tick += (_, _) =>
        {
            _deviceSettle.Stop();
            RefreshAfterDeviceChange();
        };
        Activated += (_, _) =>
        {
            if (_refreshPending)
            {
                RefreshAfterDeviceChange();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Windows broadcasts device and volume arrivals and removals to every top-level window.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(DeviceChangeHook);
        }
    }

    private IntPtr DeviceChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && wParam.ToInt64() is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
        {
            _deviceSettle.Stop();
            _deviceSettle.Start();
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Re-reads the disks after a drive came or went. Not while a dialog is open: the dialog keeps
    /// working on what it showed (the operations layer re-checks it anyway) and the list is
    /// refreshed once the user is back in the main window.
    /// </summary>
    private void RefreshAfterDeviceChange()
    {
        if (OwnedWindows.Count > 0 || DataContext is not MainViewModel viewModel)
        {
            _refreshPending = true;
            return;
        }

        _refreshPending = false;
        _ = viewModel.RefreshAsync();
    }

    /// <summary>
    /// The window opens with SizeToContent="Width" so it is exactly as wide as the toolbar and the
    /// partition table need. Once the disks are on screen that width becomes both the window's
    /// width and its minimum, sizing is handed back to the user, and the window is re-centred.
    /// Room for a vertical scroll bar is reserved so a second disk does not squeeze the table.
    /// </summary>
    public async Task LockWidthToContentAsync(Task firstLoad)
    {
        try
        {
            await firstLoad;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The window still needs a sensible width when the first read failed.
        }

        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (SizeToContent != SizeToContent.Width)
        {
            return;
        }

        var width = Math.Ceiling(ActualWidth);
        if (DiskScroller.ComputedVerticalScrollBarVisibility != Visibility.Visible)
        {
            width += SystemParameters.VerticalScrollBarWidth;
        }

        SizeToContent = SizeToContent.Manual;
        MinWidth = width;
        Width = width;

        var area = SystemParameters.WorkArea;
        Left = area.Left + Math.Max(0, (area.Width - width) / 2);
    }

    /// <summary>Opens the row's Actions menu below the button on a normal left click.</summary>
    private void Actions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// Keeps a single selection across all disk lists, so the toolbar always acts on exactly one
    /// row. Binding every list's SelectedItem to the same property would make each list clear the
    /// others, because a row of disk 0 is not an item of disk 1's list.
    /// </summary>
    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (list.SelectedItem is SegmentRow row)
        {
            if (_activeList is not null && !ReferenceEquals(_activeList, list))
            {
                _activeList.SelectedItem = null;
            }

            _activeList = list;
            viewModel.SelectedRow = row;
        }
        else if (ReferenceEquals(list, _activeList))
        {
            viewModel.SelectedRow = null;
        }
    }
}
