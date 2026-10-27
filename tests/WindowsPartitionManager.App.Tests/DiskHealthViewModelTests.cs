using System.Globalization;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.App.Tests;

public sealed class DiskHealthViewModelTests
{
    private static readonly Disk Disk = new()
    {
        Number = 0,
        FriendlyName = "Test SSD",
        BusType = "NVMe",
        SizeBytes = 1024UL * 1024 * 1024 * 1024,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        Partitions = [],
    };

    [Fact]
    public async Task Counters_AreShownWithUnits_AndTheVerdictFollowsTheAssessment()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var vm = new DiskHealthViewModel(new FixedHealth(new DiskHealth
        {
            DiskNumber = 0,
            MediaType = MediaType.Ssd,
            Health = HealthStatus.Healthy,
            CountersAvailable = true,
            TemperatureCelsius = 39,
            TemperatureMaxCelsius = 86,
            WearPercent = 75,
        }), Disk);

        await vm.LoadAsync();

        Assert.False(vm.IsLoading);
        Assert.Equal(HealthVerdict.Attention, vm.Verdict);
        Assert.Equal("Needs attention", vm.VerdictText);
        Assert.Contains(vm.Values, v => v.Label == "Temperature" && v.Value == "39 °C (drive limit 86 °C)");
        Assert.Contains(vm.Values, v => v.Label == "Wear (SSD)" && v.Value == "75 %");
        Assert.Contains(vm.Values, v => v.Label == "Read errors" && v.Value == "not reported");
    }

    [Fact]
    public async Task MissingPhysicalDisk_IsExplained()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var vm = new DiskHealthViewModel(new FixedHealth(null), Disk);

        await vm.LoadAsync();

        Assert.Equal(HealthVerdict.Unknown, vm.Verdict);
        Assert.Contains("no physical drive", Assert.Single(vm.Findings).Message, StringComparison.Ordinal);
    }

    private sealed class FixedHealth(DiskHealth? health) : IDiskHealthProvider
    {
        public Task<DiskHealth?> GetHealthAsync(int diskNumber, CancellationToken cancellationToken = default) => Task.FromResult(health);
    }
}
