using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Form for a new VHDX: where, how large, and which partition table.</summary>
public sealed class CreateVirtualDiskViewModel : INotifyPropertyChanged
{
    private readonly IVirtualDiskOperations _operations;
    private readonly Func<string, bool> _confirm;

    private string _path;
    private string _sizeText = "8 GB";
    private string _style;
    private bool _isBusy;
    private bool _isDone;
    private string _status = string.Empty;
    private string? _error;

    public CreateVirtualDiskViewModel(IVirtualDiskOperations operations, Func<string, bool> confirm)
    {
        _operations = operations;
        _confirm = confirm;
        _path = DefaultPath();
        Styles = ["GPT", "MBR", Localization.Get("Vhd_StyleNone")];
        _style = Styles[0];

        CreateCommand = new RelayCommand(() => _ = CreateAsync(), () => CanCreate);
        Revalidate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every attempt, successful or not, so the disk list can be refreshed.</summary>
    public event EventHandler? AttemptFinished;

    public IReadOnlyList<string> Styles { get; }

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public RelayCommand CreateCommand { get; }

    public string Path
    {
        get => _path;
        set
        {
            if (SetField(ref _path, value))
            {
                Revalidate();
            }
        }
    }

    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (SetField(ref _sizeText, value))
            {
                Revalidate();
            }
        }
    }

    public string Style
    {
        get => _style;
        set
        {
            if (SetField(ref _style, value))
            {
                Revalidate();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnStateChanged();
            }
        }
    }

    public bool IsDone
    {
        get => _isDone;
        private set
        {
            if (SetField(ref _isDone, value))
            {
                OnStateChanged();
            }
        }
    }

    public bool IsFormEnabled => !IsBusy && !IsDone;

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public string Summary { get; private set; } = string.Empty;

    public bool CanCreate => !IsBusy && !IsDone && !Issues.Any(i => i.IsError);

    /// <summary>Documents\Virtual disks\disk1.vhdx, or the next number that is still free.</summary>
    private static string DefaultPath()
    {
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Virtual disks");
        for (var i = 1; ; i++)
        {
            var candidate = System.IO.Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"disk{i}.vhdx"));
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private CreateVirtualDiskRequest? BuildRequest(out string? parseError)
    {
        parseError = null;
        if (!ByteSize.TryParse(SizeText, out var size) || size == 0)
        {
            parseError = Localization.Format("Common_BadSize", SizeText);
            return null;
        }

        var style = Style switch
        {
            "GPT" => PartitionStyle.Gpt,
            "MBR" => PartitionStyle.Mbr,
            _ => PartitionStyle.Raw,
        };
        return new CreateVirtualDiskRequest { Path = Path.Trim(), SizeBytes = size, Style = style };
    }

    private void Revalidate()
    {
        Issues.Clear();
        var request = BuildRequest(out var parseError);
        if (request is null)
        {
            Issues.Add(new ValidationIssue(IssueSeverity.Error, parseError!));
        }
        else
        {
            var exists = File.Exists(request.Path) || Directory.Exists(request.Path);
            foreach (var issue in VirtualDiskPlanner.ValidateCreate(request, exists, FreeBytesFor(request.Path)))
            {
                Issues.Add(issue);
            }

            Summary = Localization.Format("Vhd_Summary", ByteSize.Format(request.SizeBytes), request.Path, Style);
            OnPropertyChanged(nameof(Summary));
        }

        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.RaiseCanExecuteChanged();
    }

    private static ulong? FreeBytesFor(string path)
    {
        try
        {
            return System.IO.Path.IsPathFullyQualified(path) && System.IO.Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root) is { IsReady: true } drive
                ? (ulong)drive.AvailableFreeSpace
                : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task CreateAsync()
    {
        var request = BuildRequest(out _);
        if (request is null || !CanCreate || !_confirm(Summary + "\n\n" + Localization.Get("Vhd_ConfirmBody")))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Status = Localization.Get("Vhd_Creating");
        try
        {
            var number = await _operations.CreateAsync(request);
            Status = Localization.Format("Vhd_Created", number);
            IsDone = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Status = Localization.Get("Properties_Failed");
        }
        finally
        {
            IsBusy = false;
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(IsFormEnabled));
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
