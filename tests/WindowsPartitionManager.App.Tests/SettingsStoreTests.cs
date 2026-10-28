using System.IO;
using WindowsPartitionManager.App.Theming;

namespace WindowsPartitionManager.App.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsLanguage()
    {
        var path = NewSettingsPath();
        try
        {
            SettingsStore.Save(new AppSettings("Night", "Consolas", "Turkish"), path);

            var loaded = SettingsStore.Load(path);

            Assert.Equal("Turkish", loaded.Language);
            Assert.Equal("Night", loaded.Theme);
            Assert.Equal("Consolas", loaded.Font);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_MissingLanguageField_FallsBackToEnglish()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{\"theme\":\"Dark\",\"font\":\"Arial\"}");

            var loaded = SettingsStore.Load(path);

            Assert.Equal("English", loaded.Language);
            Assert.Equal("Dark", loaded.Theme);
            Assert.Equal("Arial", loaded.Font);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_UnknownLanguageField_FallsBackToEnglish()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "{\"language\":\"German\"}");

            var loaded = SettingsStore.Load(path);

            Assert.Equal("English", loaded.Language);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsLayout()
    {
        var path = NewSettingsPath();
        try
        {
            SettingsStore.Save(new AppSettings("White", "Segoe UI", "English", "Classic"), path);

            Assert.Equal("Classic", SettingsStore.Load(path).Layout);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData("{\"theme\":\"Dark\"}")]
    [InlineData("{\"layout\":\"Ribbon\"}")]
    public void Load_MissingOrUnknownLayout_FallsBackToModern(string json)
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, json);

            Assert.Equal("Modern", SettingsStore.Load(path).Layout);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_MalformedJson_FallsBackToDefaults()
    {
        var path = NewSettingsPath();
        try
        {
            File.WriteAllText(path, "not json");

            var loaded = SettingsStore.Load(path);

            Assert.Equal("English", loaded.Language);
            Assert.Equal("White", loaded.Theme);
            Assert.Equal("Segoe UI", loaded.Font);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string NewSettingsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wpm-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static void Cleanup(string path)
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
