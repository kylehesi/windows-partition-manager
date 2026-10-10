using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App.Theming;

/// <summary>
/// Swaps the active colour theme by replacing the theme <see cref="ResourceDictionary"/> in the
/// application's merged dictionaries, and applies the UI font by updating the shared
/// <c>UiFontFamily</c> resource. Both take effect immediately, without a restart.
/// </summary>
public static class ThemeManager
{
    private static readonly string[] Themes = ["White", "Dark", "Night"];

    private static ResourceDictionary? _currentTheme;

    public static void ApplyTheme(string themeName)
    {
        if (!Themes.Contains(themeName))
        {
            themeName = "White";
        }

        var assembly = typeof(ThemeManager).Assembly.GetName().Name;
        var uri = new Uri($"pack://application:,,,/{assembly};component/Themes/{themeName}.xaml", UriKind.Absolute);
        var dictionary = new ResourceDictionary { Source = uri };

        var merged = Application.Current!.Resources.MergedDictionaries;
        if (_currentTheme is not null)
        {
            merged.Remove(_currentTheme);
        }

        merged.Add(dictionary);
        _currentTheme = dictionary;
    }

    public static void ApplyFont(string fontName)
    {
        Application.Current!.Resources["UiFontFamily"] = new FontFamily(fontName);
    }

    public static void ApplyLanguage(string languageName)
    {
        LanguagePreference.Apply(languageName);
        ApplyLanguageToOpenWindows(LanguagePreference.ResolveCulture(languageName));
    }

    /// <summary>
    /// Points WPF's default element <c>Language</c> at the current UI culture, so binding string
    /// formatting follows it. Called once at startup: the metadata can only be overridden a single
    /// time per process, so later language changes only update the open windows' <c>Language</c>
    /// (below) and the thread cultures.
    /// </summary>
    public static void InitializeFrameworkLanguage()
    {
        if (_frameworkLanguageInitialized)
        {
            return;
        }

        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag)));
        _frameworkLanguageInitialized = true;
    }

    private static void ApplyLanguageToOpenWindows(string cultureName)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var language = XmlLanguage.GetLanguage(CultureInfo.GetCultureInfo(cultureName).IetfLanguageTag);
        foreach (Window window in app.Windows)
        {
            window.Language = language;
        }
    }

    private static bool _frameworkLanguageInitialized;
}
