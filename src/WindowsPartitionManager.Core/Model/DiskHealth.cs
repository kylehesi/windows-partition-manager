namespace WindowsPartitionManager.Core.Model;

public enum MediaType
{
    Unknown,
    Hdd,
    Ssd,
    Scm,
}

/// <summary>
/// What the drive reports about itself (Get-PhysicalDisk and Get-StorageReliabilityCounter).
/// Every counter is optional: virtual disks, many USB bridges and some controllers report none,
/// and reading them needs administrator rights.
/// </summary>
public sealed record DiskHealth
{
    public required int DiskNumber { get; init; }

    public MediaType MediaType { get; init; }

    public HealthStatus Health { get; init; } = HealthStatus.Unknown;

    /// <summary>False when the drive or the controller does not report reliability counters, or they could not be read.</summary>
    public bool CountersAvailable { get; init; }

    public int? TemperatureCelsius { get; init; }

    /// <summary>The temperature the drive itself considers its maximum, when it reports one.</summary>
    public int? TemperatureMaxCelsius { get; init; }

    /// <summary>For SSDs, the share of the rated write endurance already used, 0-100 (can exceed 100).</summary>
    public int? WearPercent { get; init; }

    public ulong? PowerOnHours { get; init; }

    public ulong? StartStopCycles { get; init; }

    public ulong? ReadErrorsTotal { get; init; }

    public ulong? ReadErrorsUncorrected { get; init; }

    public ulong? WriteErrorsTotal { get; init; }

    public ulong? WriteErrorsUncorrected { get; init; }
}
