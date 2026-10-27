using System.Globalization;
using System.IO;
using WindowsPartitionManager.App.ViewModels;

namespace WindowsPartitionManager.App.Tests;

public sealed class HistoryViewModelTests
{
    [Fact]
    public void MissingLog_ShowsTheEmptyText()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var vm = new HistoryViewModel(Path.Combine(Path.GetTempPath(), $"wpm-no-log-{Guid.NewGuid():N}.log"));

        Assert.True(vm.IsEmpty);
        Assert.StartsWith("Nothing has been changed yet", vm.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_IsReadNewestFirst_WithFriendlyNamesAndFailuresCounted()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var path = Path.Combine(Path.GetTempPath(), $"wpm-log-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path,
        [
            "2026-10-27 17:49:37 +03:00  SetLabel         disk=1 partition=2 label=\"A\"  -> OK (0.2 s)",
            "2026-10-27 17:49:40 +03:00  SomethingNew     disk=1  -> FAILED: refused",
        ]);
        try
        {
            var vm = new HistoryViewModel(path);

            Assert.Equal(2, vm.Rows.Count);
            Assert.Equal("SomethingNew", vm.Rows[0].Operation);
            Assert.False(vm.Rows[0].Succeeded);
            Assert.Equal("Rename volume", vm.Rows[1].Operation);
            Assert.Contains("1 of them failed", vm.Summary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
