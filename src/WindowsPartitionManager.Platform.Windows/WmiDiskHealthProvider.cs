using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Reads MSFT_PhysicalDisk and MSFT_StorageReliabilityCounter, the data behind Get-PhysicalDisk
/// and Get-StorageReliabilityCounter. The counters need administrator rights; without them, or
/// when the drive reports none, the result says so instead of failing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiDiskHealthProvider : IDiskHealthProvider
{
    public Task<DiskHealth?> GetHealthAsync(int diskNumber, CancellationToken cancellationToken = default)
        => Task.Run(() => GetHealth(diskNumber), cancellationToken);

    private static DiskHealth? GetHealth(int diskNumber)
    {
        var scope = Wmi.Connect();
        var id = diskNumber.ToString(CultureInfo.InvariantCulture);
        using var physical = Wmi.QuerySingle(scope, $"SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId = '{id}'");
        if (physical is null)
        {
            return null;
        }

        var health = new DiskHealth
        {
            DiskNumber = diskNumber,
            MediaType = Wmi.Get<ushort>(physical, "MediaType") switch
            {
                3 => MediaType.Hdd,
                4 => MediaType.Ssd,
                5 => MediaType.Scm,
                _ => MediaType.Unknown,
            },
            Health = Wmi.Get<ushort>(physical, "HealthStatus") switch
            {
                0 => HealthStatus.Healthy,
                1 => HealthStatus.Warning,
                2 => HealthStatus.Unhealthy,
                _ => HealthStatus.Unknown,
            },
        };

        try
        {
            // The counters cannot be enumerated on their own (the query fails); like
            // Get-StorageReliabilityCounter, reach them through their physical disk.
            using var related = physical.GetRelated("MSFT_StorageReliabilityCounter");
            foreach (var item in related)
            {
                using var counters = (ManagementObject)item;
                return health with
                {
                    CountersAvailable = true,

                    // Drives that do not measure a value report 0 for it.
                    TemperatureCelsius = Positive(counters, "Temperature"),
                    TemperatureMaxCelsius = Positive(counters, "TemperatureMax"),
                    WearPercent = (int?)Number(counters, "Wear"),
                    PowerOnHours = Number(counters, "PowerOnHours"),
                    StartStopCycles = Number(counters, "StartStopCycleCount"),
                    ReadErrorsTotal = Number(counters, "ReadErrorsTotal"),
                    ReadErrorsUncorrected = Number(counters, "ReadErrorsUncorrected"),
                    WriteErrorsTotal = Number(counters, "WriteErrorsTotal"),
                    WriteErrorsUncorrected = Number(counters, "WriteErrorsUncorrected"),
                };
            }
        }
        catch (ManagementException)
        {
            // Access denied without elevation, or a controller that does not support the class.
        }

        return health;
    }

    private static ulong? Number(ManagementBaseObject mo, string property)
        => Wmi.GetRaw(mo, property) is { } value ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : null;

    private static int? Positive(ManagementBaseObject mo, string property)
        => Number(mo, property) is { } value and > 0 ? (int)value : null;
}
