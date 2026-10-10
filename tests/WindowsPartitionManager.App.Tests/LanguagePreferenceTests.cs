using System.Globalization;
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App.Tests;

public sealed class LanguagePreferenceTests
{
    [Theory]
    [InlineData("English", "en")]
    [InlineData("Turkish", "tr")]
    [InlineData("", "en")]
    [InlineData("Klingon", "en")]
    public void ResolveCulture_MapsLanguageToCode(string language, string expected) =>
        Assert.Equal(expected, LanguagePreference.ResolveCulture(language));

    [Fact]
    public void Apply_SetsCurrentAndDefaultCulture()
    {
        var defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            LanguagePreference.Apply("Turkish");

            Assert.Equal("tr", CultureInfo.CurrentCulture.Name);
            Assert.Equal("tr", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("tr", CultureInfo.DefaultThreadCurrentCulture!.Name);
            Assert.Equal("tr", CultureInfo.DefaultThreadCurrentUICulture!.Name);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = defaultUiCulture;
        }
    }

    [Fact]
    public void FormatTimestamp_English_UsesIsoFormat()
    {
        var value = new DateTimeOffset(2026, 10, 10, 22, 14, 0, TimeSpan.Zero);

        Assert.Equal("2026-10-10 22:14", LanguagePreference.FormatTimestamp(value, CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void FormatTimestamp_Turkish_UsesCultureShortFormat()
    {
        var value = new DateTimeOffset(2026, 10, 10, 22, 14, 0, TimeSpan.Zero);

        Assert.Equal("10.10.2026 22:14", LanguagePreference.FormatTimestamp(value, CultureInfo.GetCultureInfo("tr")));
    }
}
