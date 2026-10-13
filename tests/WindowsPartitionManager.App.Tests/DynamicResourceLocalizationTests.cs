using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsPartitionManager.App;

namespace WindowsPartitionManager.App.Tests;

public sealed class DynamicResourceLocalizationTests
{
    [Fact]
    public void Apply_SwapsDictionaryAndDynamicResourceReResolves()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                Localization.Apply("English");

                var window = new Window();
                var text = new TextBlock();
                window.Content = text;
                text.SetResourceReference(TextBlock.TextProperty, "Main_Refresh");
                window.Show();
                Pump(window.Dispatcher);

                Assert.Equal("Refresh", text.Text);

                Localization.Apply("Turkish");
                Pump(window.Dispatcher);
                Assert.Equal("Yenile", OnlyMerged(app)["Main_Refresh"]);
                Assert.Equal("Yenile", text.Text);
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

    private static void Pump(Dispatcher dispatcher)
        => dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

    private static ResourceDictionary OnlyMerged(Application app)
    {
        var merged = app.Resources.MergedDictionaries;
        Assert.Single(merged);
        return merged[0];
    }
}
