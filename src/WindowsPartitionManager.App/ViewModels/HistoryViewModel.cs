using System.ComponentModel;
using System.IO;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>One row of the history window, ready to display.</summary>
public sealed record HistoryRow(string Time, string Operation, string Details, string Outcome, bool Succeeded);

/// <summary>The operation log as a table: every change Windows was asked to make, newest first.</summary>
public sealed class HistoryViewModel : INotifyPropertyChanged
{
    private readonly string _logPath;
    private IReadOnlyList<HistoryRow> _rows = [];

    public HistoryViewModel(string logPath)
    {
        _logPath = logPath;
        RefreshCommand = new RelayCommand(Load);
        Load();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand RefreshCommand { get; }

    public IReadOnlyList<HistoryRow> Rows
    {
        get => _rows;
        private set
        {
            _rows = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rows)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }

    public bool IsEmpty => Rows.Count == 0;

    public string Summary => IsEmpty
        ? Localization.Get("History_Empty")
        : Localization.Format("History_Summary", Rows.Count, Rows.Count(r => !r.Succeeded));

    /// <summary>The operation's name in the UI language; unknown names are shown as logged.</summary>
    public static string OperationName(string operation)
    {
        var key = "History_Op_" + operation;
        var text = Localization.Get(key);
        return text == key ? operation : text;
    }

    private void Load()
    {
        Rows = OperationHistory.Parse(ReadLines())
            .Select(r => new HistoryRow(LanguagePreference.FormatTimestamp(r.Time), OperationName(r.Operation), r.Details, r.Outcome, r.Succeeded))
            .ToList();
    }

    /// <summary>The log is appended to while the program runs, so it is opened for shared reading.</summary>
    private List<string> ReadLines()
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return lines;
    }
}
