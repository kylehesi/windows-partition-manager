using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.App.Theming;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Operations;
<<<<<<< HEAD
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Log locations, pending restores, version, and the appearance settings (theme, UI font and language).</summary>
=======
<<<<<<< HEAD
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Log locations, pending restores, version, and the appearance settings (theme, UI font and language).</summary>
=======

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Log locations, pending restores, version, and the appearance settings (theme and UI font).</summary>
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
>>>>>>> 12-oct-2026
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private string _restoreStatus = string.Empty;
    private string _selectedTheme;
    private string _selectedFont;
<<<<<<< HEAD
    private string _selectedLanguage;
=======
<<<<<<< HEAD
    private string _selectedLanguage;
=======
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
>>>>>>> 12-oct-2026

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly IUnblockOperations _unblock;
    private readonly Func<string, bool> _confirm;

    public SettingsViewModel(string logPath, string statePath, IUnblockOperations unblock, Func<string, bool> confirm)
    {
        LogPath = logPath;
        StatePath = statePath;
        _unblock = unblock;
        _confirm = confirm;

        var current = SettingsStore.Load();
        _selectedTheme = current.Theme;
        _selectedFont = current.Font;
<<<<<<< HEAD
        _selectedLanguage = current.Language;
=======
<<<<<<< HEAD
        _selectedLanguage = current.Language;
=======
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
>>>>>>> 12-oct-2026

        OpenLogCommand = new RelayCommand(() => Open(LogPath), () => File.Exists(LogPath));
        OpenLogFolderCommand = new RelayCommand(() => Open(Path.GetDirectoryName(LogPath)!), () => Directory.Exists(Path.GetDirectoryName(LogPath)));
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => HasPendingRestore);
    }

    public string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (informational ?? assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];
    }

    public string LogPath { get; }

    public string StatePath { get; }

    public static IReadOnlyList<string> AvailableThemes => SettingsStore.AvailableThemes;

    public static IReadOnlyList<string> AvailableFonts => SettingsStore.AvailableFonts;

<<<<<<< HEAD
    public static IReadOnlyList<string> AvailableLanguages => SettingsStore.AvailableLanguages;

=======
<<<<<<< HEAD
    public static IReadOnlyList<string> AvailableLanguages => SettingsStore.AvailableLanguages;

=======
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
>>>>>>> 12-oct-2026
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetField(ref _selectedTheme, value))
            {
                ThemeManager.ApplyTheme(value);
<<<<<<< HEAD
                SettingsStore.Save(new AppSettings(SelectedTheme, SelectedFont, SelectedLanguage));
=======
<<<<<<< HEAD
                SettingsStore.Save(new AppSettings(SelectedTheme, SelectedFont, SelectedLanguage));
=======
                SettingsStore.Save(new AppSettings(SelectedTheme, SelectedFont));
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
>>>>>>> 12-oct-2026
            }
        }
    }

    public string SelectedFont
    {
        get => _selectedFont;
        set
        {
            if (SetField(ref _selectedFont, value))
            {
                ThemeManager.ApplyFont(value);
<<<<<<< HEAD
=======
<<<<<<< HEAD
>>>>>>> 12-oct-2026
                SettingsStore.Save(new AppSettings(SelectedTheme, SelectedFont, SelectedLanguage));
            }
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetField(ref _selectedLanguage, value))
            {
                ThemeManager.ApplyLanguage(value);
                SettingsStore.Save(new AppSettings(SelectedTheme, SelectedFont, SelectedLanguage));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreText)));
<<<<<<< HEAD
=======
                Changed?.Invoke(this, EventArgs.Empty);
=======
                SettingsStore.Save(new AppSettings(SelectedTheme, SelectedFont));
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
>>>>>>> 12-oct-2026
            }
        }
    }

    public bool HasPendingRestore => _unblock.LoadState() is { HasSomethingToRestore: true };

    public string RestoreText => _unblock.LoadState() is { } s
<<<<<<< HEAD
        ? $"Settings changed on {LanguagePreference.FormatTimestamp(s.AppliedAt)} for {s.Volume}: can be restored (hibernation and page file)."
=======
<<<<<<< HEAD
        ? Localization.Format("Settings_RestoreText", LanguagePreference.FormatTimestamp(s.AppliedAt), s.Volume)
        : Localization.Get("Settings_NoRestore");
=======
        ? $"Settings changed on {s.AppliedAt:yyyy-MM-dd HH:mm} for {s.Volume}: can be restored (hibernation and page file)."
>>>>>>> 12-oct-2026
        : "No settings are waiting to be restored.";
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669

    public RelayCommand OpenLogCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public string RestoreStatus
    {
        get => _restoreStatus;
        private set
        {
            _restoreStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreStatus)));
        }
    }

    public event EventHandler? Changed;

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nothing registered to open it; not worth an error dialog.
        }
    }

    private async Task RestoreAsync()
    {
<<<<<<< HEAD
        if (!_confirm(Localization.Get("Common_RestoreConfirm")))
=======
        if (!_confirm("Restore hibernation and page file settings to what they were before Windows Partition Manager changed them?"))
>>>>>>> a89c3121d8289d5274cd7d9470051658b8569669
        {
            return;
        }

        try
        {
            await new UnblockRunner(_unblock).RestoreAsync(new Progress<string>(s => RestoreStatus = s));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RestoreStatus = ex.Message;
        }

        RestoreCommand.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPendingRestore)));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
