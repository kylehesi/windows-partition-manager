using System.Globalization;
using WindowsPartitionManager.App;

namespace WindowsPartitionManager.App.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void Get_English_ReturnsEnglishString()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

            Assert.Equal("Refresh", Localization.Get("Main_Refresh"));
            Assert.Equal("New partition", Localization.Get("Main_NewPartition"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Get_Turkish_ReturnsTurkishString()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");

            Assert.Equal("Yenile", Localization.Get("Main_Refresh"));
            Assert.Equal("Yeni bölüm", Localization.Get("Main_NewPartition"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Format_Turkish_SubstitutesArguments()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");

            Assert.Equal("Bölüm 3", Localization.Format("Segment_Partition", 3));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Get_MissingKey_ReturnsKeyItself()
    {
        Assert.Equal("No_Such_Key", Localization.Get("No_Such_Key"));
    }

    [Fact]
    public void BuildDictionary_Turkish_ContainsTurkishValues()
    {
        var dictionary = Localization.BuildDictionary(CultureInfo.GetCultureInfo("tr"));

        Assert.Equal("Yenile", dictionary["Main_Refresh"]);
        Assert.Equal("Yeni bölüm", dictionary["Main_NewPartition"]);
        Assert.Equal("Ayarlar", dictionary["Settings_Title"]);
    }

    [Fact]
    public void BuildDictionary_English_ContainsEnglishValues()
    {
        var dictionary = Localization.BuildDictionary(CultureInfo.GetCultureInfo("en"));

        Assert.Equal("Refresh", dictionary["Main_Refresh"]);
        Assert.Equal("Settings", dictionary["Settings_Title"]);
    }
}
