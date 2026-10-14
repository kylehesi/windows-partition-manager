using System.IO;
using System.Text.Json;
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App.Theming;

/// <summary>The persisted appearance choices: a colour theme, a UI font family and a language.</summary>
public sealed record AppSettings(string Theme, string Font, string Language);

/// <summary>
/// Loads and saves the appearance settings to %LOCALAPPDATA%\WindowsPartitionManager\settings.json.
/// A missing or malformed file falls back to the defaults; writes are atomic (temp file, then replace).
/// </summary>
public static class SettingsStore
{
    public const string DefaultTheme = "White";
    public const string DefaultFont = "Segoe UI";
    public const string DefaultLanguage = LanguagePreference.DefaultLanguage;

    public static readonly IReadOnlyList<string> AvailableThemes = ["White", "Dark", "Night"];
    public static readonly IReadOnlyList<string> AvailableFonts = ["Segoe UI", "Arial", "Calibri", "Consolas", "Times New Roman"];
    public static readonly IReadOnlyList<string> AvailableLanguages = LanguagePreference.AvailableLanguages;

    public static string Path { get; } = LanguagePreference.SettingsPath;

    public static AppSettings Load() => Load(Path);

    internal static AppSettings Load(string path)
    {
        var theme = DefaultTheme;
        var font = DefaultFont;
        var language = DefaultLanguage;

        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;

                if (root.TryGetProperty("theme", out var themeElement)
                    && themeElement.ValueKind == JsonValueKind.String
                    && themeElement.GetString() is { } candidateTheme
                    && AvailableThemes.Contains(candidateTheme))
                {
                    theme = candidateTheme;
                }

                if (root.TryGetProperty("font", out var fontElement)
                    && fontElement.ValueKind == JsonValueKind.String
                    && fontElement.GetString() is { } candidateFont
                    && !string.IsNullOrWhiteSpace(candidateFont))
                {
                    font = candidateFont;
                }

                if (root.TryGetProperty("language", out var languageElement)
                    && languageElement.ValueKind == JsonValueKind.String
                    && languageElement.GetString() is { } candidateLanguage
                    && AvailableLanguages.Contains(candidateLanguage))
                {
                    language = candidateLanguage;
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new AppSettings(theme, font, language);
    }

    public static void Save(AppSettings settings) => Save(settings, Path);

    internal static void Save(AppSettings settings, string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { theme = settings.Theme, font = settings.Font, language = settings.Language }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
