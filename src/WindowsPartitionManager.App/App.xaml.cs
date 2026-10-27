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
        // Developer aid for screenshots: try a theme or language without saving it.
        if (ArgumentAfter(e.Args, "--theme") is { } theme) settings = settings with { Theme = theme };
        if (ArgumentAfter(e.Args, "--language") is { } language) settings = settings with { Language = language };
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
            // A language change rebuilds the rows (their strings are read when the view models are built).
            settings.Changed += (_, _) => _ = viewModel.RefreshAsync();
            var dialog = new SettingsWindow { DataContext = settings, Owner = window };
            dialog.ShowDialog();
        };

        viewModel.DestructiveRequested += (_, form) =>
        {
            var dialog = new DestructiveActionWindow { DataContext = form, Owner = window };
            dialog.ShowDialog();
        };

        viewModel.PropertiesRequested += (_, form) =>
        {
            var dialog = new VolumePropertiesWindow { DataContext = form, Owner = window };
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
            _ = CaptureAndExitAsync(window, viewModel, layoutReady, Path.GetFullPath(screenshotPath), ArgumentAfter(e.Args, "--dialog"));
        }
    }

    /// <summary>
    /// Developer aid: <c>--screenshot file.png [--size 1120x700] [--theme Dark] [--language Turkish]
    /// [--dialog settings|history|new|resize|properties|format|delete]</c> renders the main window (or the named dialog,
    /// opened for the first row that allows it) once the disks are loaded, saves it as PNG and exits.
    /// Used to check layout without a human. Dialogs only open; nothing is ever applied.
    /// </summary>
    private async Task CaptureAndExitAsync(Window window, MainViewModel viewModel, Task refresh, string path, string? dialog)
    {
        await refresh;
        await Task.Delay(500);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var target = window;
        if (dialog is not null)
        {
            // The dialogs are modal; open them from a separate dispatcher item so this method goes on.
            _ = window.Dispatcher.BeginInvoke(() => OpenDialog(viewModel, dialog));
            await Task.Delay(1500);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            target = Windows.OfType<Window>().LastOrDefault(w => w != window) ?? window;
        }

        Capture(target, path);
        Shutdown();
    }

    private static void OpenDialog(MainViewModel viewModel, string dialog)
    {
        var rows = viewModel.Disks.SelectMany(d => d.Rows).ToList();
        switch (dialog.ToLowerInvariant())
        {
            case "settings":
                viewModel.SettingsCommand.Execute(null);
                break;
            case "new" when rows.FirstOrDefault(r => r.CanCreate) is { } row:
                viewModel.NewPartitionCommand.Execute(row);
                break;
            case "resize" when rows.FirstOrDefault(r => r.CanResize) is { } row:
                viewModel.ResizeCommand.Execute(row);
                break;
            case "format" when rows.FirstOrDefault(r => r.CanFormat) is { } row:
                viewModel.FormatCommand.Execute(row);
                break;
            case "history":
                new HistoryWindow { DataContext = new HistoryViewModel(OperationLog.Path), Owner = Current.MainWindow }.ShowDialog();
                break;
            case "properties" when rows.FirstOrDefault(r => r.CanEditProperties) is { } row:
                viewModel.PropertiesCommand.Execute(row);
                break;
            case "delete" when rows.FirstOrDefault(r => r.CanDelete) is { } row:
                viewModel.DeleteCommand.Execute(row);
                break;
        }
    }

    private static void Capture(Window window, string path)
    {
        if (window.Content is not FrameworkElement content)
        {
            return;
        }

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
