using System.Globalization;
using WindowsPartitionManager.App.Theming;

namespace WindowsPartitionManager.App.Tests;

public sealed class ThemeManagerTests
{
    [Fact]
    public void ApplyLanguage_SetsCurrentAndDefaultCulture()
    {
        var defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            ThemeManager.ApplyLanguage("Turkish");

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
}
