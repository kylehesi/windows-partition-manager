using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WindowsPartitionManager.App.Controls;

public static class Converters
{
    public static readonly IValueConverter BoolToVisibility = new BoolToVisibilityConverter();

    /// <summary>
    /// Shows a value through the string table: the key is the converter parameter (a prefix such
    /// as <c>Theme_</c>) followed by the value. Values without an entry are shown as they are.
    /// </summary>
    public static readonly IValueConverter Localized = new LocalizedConverter();

    private sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is Visibility.Visible;
    }

    private sealed class LocalizedConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value?.ToString() ?? string.Empty;
            var key = parameter + text;
            var localized = Localization.Get(key);
            return localized == key ? text : localized;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
