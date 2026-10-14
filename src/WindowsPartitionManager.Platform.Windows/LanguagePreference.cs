using System.Globalization;
using System.Text.Json;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// The user's language choice: where it is stored, how it maps to a culture, and how dates follow
/// it. Shared by the desktop app and the command-line tool so both honour the same setting.
/// </summary>
public static class LanguagePreference
{
    public const string DefaultLanguage = "English";

    public static readonly IReadOnlyList<string> AvailableLanguages = ["English", "Turkish"];

    public static string SettingsPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsPartitionManager",
        "settings.json");

    public static string LoadLanguage() => LoadLanguage(SettingsPath);

    internal static string LoadLanguage(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("language", out var language)
                    && language.ValueKind == JsonValueKind.String
                    && language.GetString() is { } candidate
                    && AvailableLanguages.Contains(candidate))
                {
                    return candidate;
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

        return DefaultLanguage;
    }

    public static void Apply(string languageName)
    {
        var culture = new CultureInfo(ResolveCulture(languageName));
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string ResolveCulture(string languageName) =>
        languageName switch
        {
            "Turkish" => "tr",
            _ => "en",
        };

    /// <summary>
    /// Formats a timestamp in the current culture, except that English keeps the ISO-style
    /// <c>yyyy-MM-dd HH:mm</c> form the program has always shown.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return culture.TwoLetterISOLanguageName == "en"
            ? value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value.ToString("g", culture);
    }
}
