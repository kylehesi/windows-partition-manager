using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Formatting;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>One line of the space list: a folder to open, a file, or the rest of a folder's files.</summary>
public sealed record SpaceItem(string Name, string Size, double Fraction, string Detail, bool IsFolder, SpaceFolder? Folder, string? FullPath)
{
    /// <summary>Segoe Fluent glyph: folder, document, or a stack for "other files".</summary>
    public string Glyph => IsFolder ? "\uE8B7" : FullPath is null ? "\uE8FD" : "\uE7C3";

    public bool HasDetail => Detail.Length > 0;
}

/// <summary>What takes the space on a volume, folder by folder. Read-only; nothing is changed.</summary>
public sealed class SpaceAnalysisViewModel : INotifyPropertyChanged
{
    private readonly IVolumeSpaceReader _reader;
    private readonly char _letter;
    private SpaceReport? _report;
    private SpaceFolder? _current;
    private IReadOnlyList<SpaceItem> _items = [];
    private bool _isLoading = true;
    private bool _showLargestFiles;
    private string _status = string.Empty;
    private string? _error;

    public SpaceAnalysisViewModel(IVolumeSpaceReader reader, char letter)
    {
        _reader = reader;
        _letter = char.ToUpperInvariant(letter);
        UpCommand = new RelayCommand(() => Navigate(_current?.Parent), () => _current?.Parent is not null && !ShowLargestFiles);
        OpenCommand = new RelayCommand<SpaceItem>(Open, item => item.IsFolder || item.FullPath is not null);
        ExplorerCommand = new RelayCommand(() => OpenInExplorer(CurrentFullPath), () => _report is not null);
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !IsLoading);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => Localization.Format("Space_Title", _letter);

    public RelayCommand UpCommand { get; }

    public RelayCommand<SpaceItem> OpenCommand { get; }

    public RelayCommand ExplorerCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            _isLoading = value;
            Changed(nameof(IsLoading));
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Changed(nameof(Status));
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            _error = value;
            Changed(nameof(Error));
            Changed(nameof(HasError));
        }
    }

    public bool HasError => Error is not null;

    public string Summary => _report is { } r
        ? Localization.Format("Space_Summary", ByteSize.Format(r.Root.SizeBytes), r.Files.ToString("N0", CultureInfo.CurrentCulture), r.Folders.ToString("N0", CultureInfo.CurrentCulture), r.Elapsed.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>"C:\Program Files (x86)\Steam", or the heading of the largest-files view.</summary>
    public string Location => ShowLargestFiles ? Localization.Get("Space_LargestFilesHeading") : CurrentFullPath;

    public IReadOnlyList<SpaceItem> Items
    {
        get => _items;
        private set
        {
            _items = value;
            Changed(nameof(Items));
        }
    }

    /// <summary>Switches between the folder view and the 100 largest files of the whole volume.</summary>
    public bool ShowLargestFiles
    {
        get => _showLargestFiles;
        set
        {
            _showLargestFiles = value;
            Changed(nameof(ShowLargestFiles));
            Rebuild();
        }
    }

    private string CurrentFullPath => $@"{_letter}:\" + (_current?.Path ?? string.Empty);

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var progress = new Progress<InspectProgress>(p => Status = p.Done > 0
                ? Localization.Format("Space_Reading", p.Done.ToString("N0", CultureInfo.CurrentCulture))
                : p.Stage);
            var entries = await _reader.ReadAsync(_letter, progress);
            _report = await Task.Run(() => SpaceAnalyzer.Build(_letter, entries, stopwatch.Elapsed));
            Changed(nameof(Summary));
            ExplorerCommand.RaiseCanExecuteChanged();
            Navigate(_report.Root);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
        }
        finally
        {
            Status = string.Empty;
            IsLoading = false;
        }
    }

    private void Open(SpaceItem item)
    {
        if (item.Folder is { } folder)
        {
            ShowLargestFilesSilently(false);
            Navigate(folder);
        }
        else if (item.FullPath is { } path)
        {
            // Show the file selected in its folder rather than opening it.
            OpenInExplorer("/select,\"" + path + "\"");
        }
    }

    private void ShowLargestFilesSilently(bool value)
    {
        _showLargestFiles = value;
        Changed(nameof(ShowLargestFiles));
    }

    private void Navigate(SpaceFolder? folder)
    {
        if (folder is null)
        {
            return;
        }

        _current = folder;
        Rebuild();
    }

    private void Rebuild()
    {
        if (_report is null || _current is null)
        {
            return;
        }

        Items = ShowLargestFiles ? LargestFiles(_report) : Contents(_current);
        Changed(nameof(Location));
        UpCommand.RaiseCanExecuteChanged();
    }

    private List<SpaceItem> Contents(SpaceFolder folder)
    {
        var total = Math.Max(1, folder.SizeBytes);
        var items = folder.Folders.Select(f => new SpaceItem(
            f.Name,
            ByteSize.Format(f.SizeBytes),
            (double)f.SizeBytes / total,
            Localization.Format("Space_FolderDetail", f.FileCount.ToString("N0", CultureInfo.CurrentCulture)),
            IsFolder: true,
            f,
            null)).ToList();

        items.AddRange(folder.LargestFiles.Select(f => new SpaceItem(f.Name, ByteSize.Format(f.SizeBytes), (double)f.SizeBytes / total, string.Empty, false, null, $@"{_letter}:\{f.Path}")));
        if (folder.OtherFilesCount > 0)
        {
            items.Add(new SpaceItem(
                Localization.Format("Space_OtherFiles", folder.OtherFilesCount.ToString("N0", CultureInfo.CurrentCulture)),
                ByteSize.Format(folder.OtherFilesBytes),
                (double)folder.OtherFilesBytes / total,
                string.Empty,
                false,
                null,
                null));
        }

        // Folders and files mixed, largest first, the way the space is actually spread.
        return [.. items.OrderByDescending(i => i.Fraction)];
    }

    private List<SpaceItem> LargestFiles(SpaceReport report)
    {
        var total = Math.Max(1, report.Root.SizeBytes);
        return report.LargestFiles
            .Select(f => new SpaceItem(f.Name, ByteSize.Format(f.SizeBytes), (double)f.SizeBytes / total, System.IO.Path.GetDirectoryName(f.Path) is { Length: > 0 } dir ? $@"{_letter}:\{dir}" : $@"{_letter}:\", false, null, $@"{_letter}:\{f.Path}"))
            .ToList();
    }

    private static void OpenInExplorer(string argument)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Explorer missing or refused; nothing worth an error dialog.
        }
    }

    private void Changed(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
