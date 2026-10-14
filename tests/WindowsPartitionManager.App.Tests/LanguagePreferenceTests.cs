using System.Globalization;
using System.IO;
using WindowsPartitionManager.Platform.Windows;

namespace WindowsPartitionManager.App.Tests;

public sealed class LanguagePreferenceTests
{
    [Theory]
    [InlineData("{\"language\":\"Turkish\"}", "Turkish")]
    [InlineData("{\"language\":\"English\"}", "English")]
    [InlineData("{\"language\":\"German\"}", "English")]
    [InlineData("{\"theme\":\"Dark\"}", "English")]
    [InlineData("not json", "English")]
    public void LoadLanguage_PathSeam_FallsBackToEnglishWhenNotTurkish(string contents, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "wpm-language-tests", Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, contents);

            Assert.Equal(expected, LanguagePreference.LoadLanguage(path));
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void LoadLanguage_MissingFile_ReturnsEnglish()
    {
        var path = Path.Combine(Path.GetTempPath(), "wpm-language-tests", Guid.NewGuid().ToString("N"), "settings.json");

        Assert.Equal("English", LanguagePreference.LoadLanguage(path));
    }

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

    [Fact]
    public void Apply_Turkish_NumberOutputFollowsCulture()
    {
        // The CLI formats counts with "N0" under the culture Apply sets (e.g. `wpm layout`).
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            LanguagePreference.Apply("Turkish");

            Assert.Equal("1.234.567", 1234567.ToString("N0", CultureInfo.CurrentCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
