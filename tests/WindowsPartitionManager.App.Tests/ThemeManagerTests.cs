using System.Globalization;
<<<<<<< HEAD
=======
using System.Threading;
using System.Windows;
>>>>>>> 12-oct-2026
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
<<<<<<< HEAD
=======

    [Fact]
    public void ApplyLanguageToWindows_SetsEveryWindowLanguage()
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var first = new Window();
                var second = new Window();

                ThemeManager.ApplyLanguageToWindows([first, second], "tr");

                Assert.Equal("tr", first.Language.IetfLanguageTag);
                Assert.Equal("tr", second.Language.IetfLanguageTag);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
>>>>>>> 12-oct-2026
}
