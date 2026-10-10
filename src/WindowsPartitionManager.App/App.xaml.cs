using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsPartitionManager.App.Theming;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App;

public partial class App : Application
{
    private const string ProductName = "Windows Partition Manager";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = SettingsStore.Load();
        ThemeManager.ApplyTheme(settings.Theme);
        ThemeManager.ApplyFont(settings.Font);
        ThemeManager.ApplyLanguage(settings.Language);
        ThemeManager.InitializeFrameworkLanguage();

        var storage = new WmiStorageProvider();
        MainWindow? window = null;

        bool Confirm(string message) => MessageBox.Show(window, message, ProductName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        var viewModel = new MainViewModel(storage, new NtfsVolumeInspector(), storage, new WmiStorageOperations(), new PowerStatusProvider(), new UnblockOperations(), Confirm);
        window = new MainWindow { DataContext = viewModel };

        viewModel.AnalysisRequested += (_, analysis) =>
        {
            var dialog = new ShrinkAnalysisWindow { DataContext = analysis, Owner = window };
            dialog.Closed += (_, _) => analysis.Dispose();
            analysis.UnblockRequested += (_, wizard) =>
            {
                var wizardWindow = new UnblockWindow { DataContext = wizard, Owner = dialog };
                wizardWindow.ShowDialog();
            };
            dialog.Show();
            _ = analysis.AnalyzeAsync();
        };

        viewModel.NewPartitionRequested += (_, form) =>
        {
            var dialog = new NewPartitionWindow { DataContext = form, Owner = window };
            dialog.ShowDialog();
        };

        viewModel.SettingsRequested += (_, _) =>
        {
            var settings = new SettingsViewModel(OperationLog.Path, UnblockOperations.StatePath, new UnblockOperations(), Confirm);
            var dialog = new SettingsWindow { DataContext = settings, Owner = window };
            dialog.ShowDialog();
        };

        viewModel.DestructiveRequested += (_, form) =>
        {
            var dialog = new DestructiveActionWindow { DataContext = form, Owner = window };
            dialog.ShowDialog();
        };

        viewModel.ResizeRequested += (_, form) =>
        {
            var dialog = new ResizePartitionWindow { DataContext = form, Owner = window };
            _ = form.LoadLimitsAsync();
            dialog.ShowDialog();
        };

        var screenshotPath = ArgumentAfter(e.Args, "--screenshot");
        var fixedSize = false;
        if (ArgumentAfter(e.Args, "--size") is { } size && TryParseSize(size, out var width, out var height))
        {
            window.SizeToContent = SizeToContent.Manual;
            window.Width = width;
            window.Height = height;
            fixedSize = true;
        }

        MainWindow = window;
        window.Show();

        var refresh = viewModel.RefreshAsync();
        var layoutReady = fixedSize ? refresh : window.LockWidthToContentAsync(refresh);
        if (screenshotPath is not null)
        {
            _ = CaptureAndExitAsync(window, layoutReady, Path.GetFullPath(screenshotPath));
        }
    }

    /// <summary>
    /// Developer aid: <c>--screenshot file.png [--size 1120x700]</c> renders the main window once
    /// the disks are loaded, saves it as PNG and exits. Used to check layout without a human.
    /// </summary>
    private async Task CaptureAndExitAsync(Window window, Task refresh, string path)
    {
        await refresh;
        await Task.Delay(500);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        if (window.Content is FrameworkElement content)
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, bounds);
                dc.DrawRectangle(new VisualBrush(content), null, bounds);
            }

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX),
                (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        Shutdown();
    }

    private static string? ArgumentAfter(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool TryParseSize(string text, out double width, out double height)
    {
        width = height = 0;
        var parts = text.Split('x', 'X');
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out width)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out height);
    }
}
