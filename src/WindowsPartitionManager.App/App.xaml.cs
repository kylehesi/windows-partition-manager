using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsPartitionManager.App.Theming;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Operations;
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
        if (ArgumentAfter(e.Args, "--layout") is { } layout) settings = settings with { Layout = layout };
        ThemeManager.ApplyTheme(settings.Theme);
        ThemeManager.ApplyFont(settings.Font);
        ThemeManager.ApplyLanguage(settings.Language);
        ThemeManager.InitializeFrameworkLanguage();

        var storage = new WmiStorageProvider();

        bool Confirm(string message) => MessageBox.Show(MainWindow, message, ProductName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        var operations = new WmiStorageOperations();
        var viewModel = new MainViewModel(storage, new NtfsVolumeInspector(), storage, operations, new PowerStatusProvider(), new UnblockOperations(), new VirtualDiskOperations(operations), new WmiDiskHealthProvider(), new NtfsSpaceReader(), Confirm);
        var currentLayout = settings.Layout;

        // Both layouts are views of the same view model; switching one for the other keeps the
        // disks, the selection and the queue.
        Window CreateMainWindow(string layout) => layout == "Classic"
            ? new MainWindow { DataContext = viewModel }
            : new ModernMainWindow { DataContext = viewModel };

        void SwitchLayout(string layout)
        {
            var old = MainWindow;
            var next = CreateMainWindow(layout);
            next.Language = old.Language;
            viewModel.SelectedRow = null;
            MainWindow = next;
            next.Show();
            old.Close();
            if (next is MainWindow classic)
            {
                _ = classic.LockWidthToContentAsync(Task.CompletedTask);
            }
        }

        var window = CreateMainWindow(currentLayout);

        viewModel.AnalysisRequested += (_, analysis) =>
        {
            var dialog = new ShrinkAnalysisWindow { DataContext = analysis, Owner = MainWindow };
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
            var dialog = new NewPartitionWindow { DataContext = form, Owner = MainWindow };
            dialog.ShowDialog();
        };

        viewModel.SettingsRequested += (_, _) =>
        {
            var settings = new SettingsViewModel(OperationLog.Path, UnblockOperations.StatePath, new UnblockOperations(), Confirm);
            // A language change rebuilds the rows (their strings are read when the view models are built).
            settings.Changed += (_, _) => _ = viewModel.RefreshAsync();
            var dialog = new SettingsWindow { DataContext = settings, Owner = MainWindow };
            dialog.ShowDialog();
            if (settings.SelectedLayout != currentLayout)
            {
                currentLayout = settings.SelectedLayout;
                SwitchLayout(currentLayout);
            }
        };

        viewModel.DestructiveRequested += (_, form) =>
        {
            var dialog = new DestructiveActionWindow { DataContext = form, Owner = MainWindow };
            dialog.ShowDialog();
        };

        viewModel.HealthRequested += (_, health) =>
        {
            var dialog = new DiskHealthWindow { DataContext = health, Owner = MainWindow };
            _ = health.LoadAsync();
            dialog.ShowDialog();
        };

        viewModel.CreateVirtualDiskRequested += (_, form) =>
        {
            var dialog = new CreateVirtualDiskWindow { DataContext = form, Owner = MainWindow };
            dialog.ShowDialog();
        };

        viewModel.AttachVirtualDiskRequested += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = Localization.Get("Vhd_FileFilterAll"), CheckFileExists = true };
            if (picker.ShowDialog(MainWindow) == true && Confirm(Localization.Format("Vhd_AttachPrompt", picker.FileName)))
            {
                _ = viewModel.AttachVirtualDiskAsync(picker.FileName);
            }
        };

        viewModel.SpaceRequested += (_, space) =>
        {
            var dialog = new SpaceAnalysisWindow { DataContext = space, Owner = MainWindow };
            _ = space.LoadAsync();
            dialog.Show(); // not modal: it can stay open next to the main window
        };

        viewModel.OptimizeRequested += (_, form) =>
        {
            var dialog = new OptimizeVolumeWindow { DataContext = form, Owner = MainWindow };
            dialog.ShowDialog();
        };

        viewModel.CheckRequested += (_, form) =>
        {
            var dialog = new CheckVolumeWindow { DataContext = form, Owner = MainWindow };
            dialog.ShowDialog();
        };

        viewModel.PropertiesRequested += (_, form) =>
        {
            var dialog = new VolumePropertiesWindow { DataContext = form, Owner = MainWindow };
            dialog.ShowDialog();
        };

        viewModel.ResizeRequested += (_, form) =>
        {
            var dialog = new ResizePartitionWindow { DataContext = form, Owner = MainWindow };
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
        var layoutReady = !fixedSize && window is MainWindow classicWindow ? classicWindow.LockWidthToContentAsync(refresh) : refresh;
        if (screenshotPath is not null)
        {
            _ = CaptureAndExitAsync(window, viewModel, layoutReady, Path.GetFullPath(screenshotPath), ArgumentAfter(e.Args, "--dialog"));
        }
    }

    /// <summary>
    /// Developer aid: <c>--screenshot file.png [--size 1120x700] [--theme Dark] [--language Turkish] [--layout Classic]
    /// [--dialog select|settings|history|health|plan|vhd|new|resize|properties|check|optimize|space|format|delete]</c> renders the main window (or the named dialog,
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
            case "plan" when rows.FirstOrDefault(r => r.CanCreate) is { } row:
                // Paper only: shows the queue panel and the projected layout; nothing is applied.
                viewModel.Plan(new PlannedCreate(row.DiskNumber, PartitionPlanner.Normalize(new CreatePartitionRequest { DiskNumber = row.DiskNumber, OffsetBytes = row.Segment.OffsetBytes, SizeBytes = 512UL * 1024 * 1024 }).OffsetBytes, 512UL * 1024 * 1024, "NTFS", "Planned", null));
                break;
            case "select" when rows.FirstOrDefault(r => r.CanResize) is { } row:
                // Not a dialog: selects a partition so the details panel shows.
                viewModel.SelectedRow = row;
                break;
            case "health" when viewModel.Disks.FirstOrDefault() is { } disk:
                viewModel.HealthCommand.Execute(disk.Disk);
                break;
            case "vhd":
                viewModel.CreateVirtualDiskCommand.Execute(null);
                break;
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
            case "space" when rows.LastOrDefault(r => r.CanAnalyze) is { } row:
                viewModel.SpaceCommand.Execute(row);
                break;
            case "optimize" when rows.FirstOrDefault(r => r.CanOptimize) is { } row:
                viewModel.OptimizeCommand.Execute(row);
                break;
            case "check" when rows.FirstOrDefault(r => r.CanCheck) is { } row:
                viewModel.CheckCommand.Execute(row);
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
