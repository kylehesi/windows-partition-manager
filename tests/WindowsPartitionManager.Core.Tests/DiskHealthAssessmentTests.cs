using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Tests;

public class DiskHealthAssessmentTests
{
    private static DiskHealth Ssd() => new()
    {
        DiskNumber = 0,
        MediaType = MediaType.Ssd,
        Health = HealthStatus.Healthy,
        CountersAvailable = true,
        TemperatureCelsius = 42,
        WearPercent = 3,
        PowerOnHours = 4200,
        ReadErrorsUncorrected = 0,
        WriteErrorsUncorrected = 0,
    };

    [Fact]
    public void HealthyDrive_IsGood_WithoutOverclaiming()
    {
        var (verdict, findings) = DiskHealthAssessment.Assess(Ssd());

        Assert.Equal(HealthVerdict.Good, verdict);
        Assert.Contains("Nothing the drive reports", Assert.Single(findings).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UncorrectedErrors_AreBad()
    {
        var (verdict, findings) = DiskHealthAssessment.Assess(Ssd() with { ReadErrorsUncorrected = 2, WriteErrorsUncorrected = 1 });

        Assert.Equal(HealthVerdict.Bad, verdict);
        Assert.Contains(findings, f => f.Message.Contains('3', StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(69, HealthVerdict.Good)]
    [InlineData(70, HealthVerdict.Attention)]
    [InlineData(95, HealthVerdict.Bad)]
    public void Wear_IsGradual(int wear, HealthVerdict expected)
    {
        Assert.Equal(expected, DiskHealthAssessment.Assess(Ssd() with { WearPercent = wear }).Verdict);
    }

    [Fact]
    public void Temperature_UsesTheDrivesOwnLimit_ElseOneByMediaType()
    {
        Assert.Equal(HealthVerdict.Attention, DiskHealthAssessment.Assess(Ssd() with { TemperatureCelsius = 71 }).Verdict);
        Assert.Equal(HealthVerdict.Good, DiskHealthAssessment.Assess(Ssd() with { TemperatureCelsius = 71, TemperatureMaxCelsius = 80 }).Verdict);
        Assert.Equal(HealthVerdict.Attention, DiskHealthAssessment.Assess(Ssd() with { MediaType = MediaType.Hdd, TemperatureCelsius = 56 }).Verdict);
    }

    [Fact]
    public void WindowsVerdict_Counts_AndBadOutranksAttention()
    {
        Assert.Equal(HealthVerdict.Attention, DiskHealthAssessment.Assess(Ssd() with { Health = HealthStatus.Warning }).Verdict);
        Assert.Equal(HealthVerdict.Bad, DiskHealthAssessment.Assess(Ssd() with { Health = HealthStatus.Warning, WearPercent = 99 }).Verdict);
        Assert.Equal(HealthVerdict.Bad, DiskHealthAssessment.Assess(Ssd() with { Health = HealthStatus.Unhealthy }).Verdict);
    }

    [Fact]
    public void NoCounters_IsGoodOnlyWhenWindowsSaysHealthy()
    {
        var bare = new DiskHealth { DiskNumber = 1, Health = HealthStatus.Healthy };
        Assert.Equal(HealthVerdict.Good, DiskHealthAssessment.Assess(bare).Verdict);
        Assert.Equal(HealthVerdict.Unknown, DiskHealthAssessment.Assess(bare with { Health = HealthStatus.Unknown }).Verdict);
    }
}
