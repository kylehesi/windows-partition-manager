using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Windows;
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App;

/// <summary>
/// The localizable strings of the desktop app. <c>Strings.resx</c> is the neutral (English) set
/// and <c>Strings.tr.resx</c> the Turkish satellite. XAML reads them through the
/// <see cref="ResourceDictionary"/> that <see cref="Apply"/> swaps into the application's merged
/// dictionaries (the same pattern as the theme); view models read them with <see cref="Get"/>.
/// </summary>
public static class Localization
{
    private static readonly ResourceManager Manager =
        new("WindowsPartitionManager.App.Strings", typeof(Localization).Assembly);

    private static readonly ConcurrentDictionary<string, CompositeFormat> FormatCache = new(StringComparer.Ordinal);

    private static ResourceDictionary? _current;

    /// <summary>Returns the string for <paramref name="key"/> in the current UI culture, or the key itself when missing.</summary>
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>
    /// Returns the string for <paramref name="key"/> with the arguments substituted. The arguments
    /// are already localized or pre-formatted, so an invariant culture keeps numbers and dates from
    /// being re-formatted a second time.
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        var format = Get(key);
        var composite = FormatCache.GetOrAdd(format, static f => CompositeFormat.Parse(f));
        return string.Format(CultureInfo.InvariantCulture, composite, args);
    }

    /// <summary>Rebuilds the string dictionary for the given language and swaps it into the app's merged dictionaries.</summary>
    public static void Apply(string languageName)
    {
        var culture = new CultureInfo(LanguagePreference.ResolveCulture(languageName));
        CultureInfo.CurrentUICulture = culture;

        if (Application.Current is not { } app || !app.Dispatcher.CheckAccess())
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        if (_current is not null)
        {
            merged.Remove(_current);
        }

        var dictionary = BuildDictionary(culture);
        merged.Add(dictionary);
        _current = dictionary;
    }

    /// <summary>Every key and its value for the given culture, ready to be read by <c>DynamicResource</c>.</summary>
    internal static ResourceDictionary BuildDictionary(CultureInfo culture)
    {
        var dictionary = new ResourceDictionary();
        var set = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: true);
        if (set is not null)
        {
            foreach (DictionaryEntry entry in set)
            {
                dictionary[entry.Key.ToString()!] = entry.Value!;
            }
        }

        return dictionary;
    }
}
