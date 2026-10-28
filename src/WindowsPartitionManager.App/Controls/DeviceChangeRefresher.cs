using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsPartitionManager.App.ViewModels;

namespace WindowsPartitionManager.App.Controls;

/// <summary>
/// Re-reads the disks when a drive is plugged in or removed. Windows broadcasts WM_DEVICECHANGE to
/// every top-level window; the list is refreshed once the burst of notifications has settled, and
/// not while a dialog is open (the dialog keeps working on what it showed; the operations layer
/// re-checks it anyway) but as soon as the user is back in the main window.
/// </summary>
internal sealed class DeviceChangeRefresher
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private readonly Window _window;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _pending;

    private DeviceChangeRefresher(Window window)
    {
        _window = window;
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Refresh();
        };
        window.Activated += (_, _) =>
        {
            if (_pending)
            {
                Refresh();
            }
        };
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook(Hook);
            }
        };
    }

    /// <summary>Call from the window's constructor, before it is shown.</summary>
    public static void Attach(Window window) => _ = new DeviceChangeRefresher(window);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && wParam.ToInt64() is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
        {
            _settle.Stop();
            _settle.Start();
        }

        return IntPtr.Zero;
    }

    private void Refresh()
    {
        if (_window.OwnedWindows.Count > 0 || _window.DataContext is not MainViewModel viewModel)
        {
            _pending = true;
            return;
        }

        _pending = false;
        _ = viewModel.RefreshAsync();
    }
}
