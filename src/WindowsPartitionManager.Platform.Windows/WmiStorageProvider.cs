using System.Management;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Reads disks, partitions and volumes from the Windows Storage Management API
/// (WMI namespace <c>root\Microsoft\Windows\Storage</c>), the same source the
/// PowerShell Get-Disk / Get-Partition / Get-Volume cmdlets use.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiStorageProvider : IStorageProvider, ISupportedSizeProvider
{
    public Task<IReadOnlyList<Disk>> GetDisksAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<Disk>>(() => GetDisks(cancellationToken), cancellationToken);

    /// <summary>
    /// MSFT_Partition.GetSupportedSize: the same numbers Disk Management shows as the shrink / extend
    /// limits. Windows performs its own (slow) shrink analysis to produce them.
    /// </summary>
    public Task<SupportedSize?> GetSupportedSizeAsync(int diskNumber, int partitionNumber, CancellationToken cancellationToken = default)
        => Task.Run(() => GetSupportedSize(diskNumber, partitionNumber), cancellationToken);

    internal static List<Disk> GetDisks(CancellationToken cancellationToken)
    {
        var scope = Wmi.Connect();

        var volumesByPath = new Dictionary<string, Volume>(StringComparer.OrdinalIgnoreCase);
        var bitLocker = BitLockerReader.Read();
        foreach (var volume in Wmi.Query(scope, "SELECT * FROM MSFT_Volume").Select(ReadVolume))
        {
            if (volume is not null)
            {
                volumesByPath[volume.Path] = bitLocker.TryGetValue(volume.Path, out var status) ? volume with { BitLocker = status } : volume;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var partitionsByDisk = new Dictionary<int, List<Partition>>();
        foreach (var (diskNumber, partition) in Wmi.Query(scope, "SELECT * FROM MSFT_Partition").Select(mo => ReadPartition(mo, volumesByPath)))
        {
            if (!partitionsByDisk.TryGetValue(diskNumber, out var list))
            {
                list = [];
                partitionsByDisk[diskNumber] = list;
            }

            list.Add(partition);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Wmi.Query(scope, "SELECT * FROM MSFT_Disk")
            .Select(mo => ReadDisk(mo, partitionsByDisk))
            .OrderBy(d => d.Number)
            .ToList();
    }

    private static SupportedSize? GetSupportedSize(int diskNumber, int partitionNumber)
    {
        var scope = Wmi.Connect();
        var wql = $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}";

        using var partition = Wmi.QuerySingle(scope, wql);
        if (partition is null)
        {
            return null;
        }

        try
        {
            using var outParams = Wmi.Invoke(partition, "GetSupportedSize");
            return new SupportedSize(Wmi.Get<ulong>(outParams, "SizeMin"), Wmi.Get<ulong>(outParams, "SizeMax"));
        }
        catch (StorageOperationException)
        {
            return null;
        }
    }

    /// <summary>For an attached VHD/VHDX (bus type 14 or 15) Windows reports the file as the disk's location.</summary>
    private static string? VirtualDiskPathOf(ManagementBaseObject mo)
    {
        if (Wmi.Get<ushort>(mo, "BusType") is not (14 or 15) || Wmi.Get<string>(mo, "Location") is not { } location)
        {
            return null;
        }

        location = location.Trim();
        return location.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || location.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)
            ? location
            : null;
    }

    private static Disk ReadDisk(ManagementBaseObject mo, Dictionary<int, List<Partition>> partitionsByDisk)
    {
        var number = (int)Wmi.Get<uint>(mo, "Number");
        var partitions = partitionsByDisk.TryGetValue(number, out var list)
            ? list.OrderBy(p => p.OffsetBytes).ToList()
            : [];

        return new Disk
        {
            Number = number,
            FriendlyName = Wmi.Get<string>(mo, "FriendlyName")?.Trim() is { Length: > 0 } name ? name : $"Disk {number}",
            SerialNumber = Wmi.Get<string>(mo, "SerialNumber")?.Trim(),
            BusType = PartitionTypes.BusTypeName(Wmi.Get<ushort>(mo, "BusType")),
            SizeBytes = Wmi.Get<ulong>(mo, "Size"),
            LogicalSectorSize = Wmi.Get<uint>(mo, "LogicalSectorSize"),
            PhysicalSectorSize = Wmi.Get<uint>(mo, "PhysicalSectorSize"),
            Style = Wmi.Get<ushort>(mo, "PartitionStyle") switch
            {
                1 => PartitionStyle.Mbr,
                2 => PartitionStyle.Gpt,
                _ => PartitionStyle.Raw,
            },
            IsSystem = Wmi.Get<bool>(mo, "IsSystem"),
            IsBoot = Wmi.Get<bool>(mo, "IsBoot"),
            IsReadOnly = Wmi.Get<bool>(mo, "IsReadOnly"),
            IsOffline = Wmi.Get<bool>(mo, "IsOffline"),
            Health = Wmi.Get<ushort>(mo, "HealthStatus") switch
            {
                0 => HealthStatus.Healthy,
                1 => HealthStatus.Warning,
                2 => HealthStatus.Unhealthy,
                _ => HealthStatus.Unknown,
            },
            LargestFreeExtentBytes = Wmi.Get<ulong>(mo, "LargestFreeExtent"),
            VirtualDiskPath = VirtualDiskPathOf(mo),
            Partitions = partitions,
        };
    }

    internal static (int DiskNumber, Partition Partition) ReadPartition(ManagementBaseObject mo, IReadOnlyDictionary<string, Volume> volumesByPath)
    {
        var diskNumber = (int)Wmi.Get<uint>(mo, "DiskNumber");

        var gptType = Wmi.Get<string>(mo, "GptType");
        var (kind, description) = gptType is not null && Guid.TryParse(gptType, out var gptGuid)
            ? PartitionTypes.FromGpt(gptGuid)
            : PartitionTypes.FromMbr(Wmi.Get<ushort>(mo, "MbrType"));

        Volume? volume = null;
        if (Wmi.GetRaw(mo, "AccessPaths") is string[] accessPaths)
        {
            foreach (var path in accessPaths)
            {
                if (volumesByPath.TryGetValue(path, out volume))
                {
                    break;
                }
            }
        }

        var partition = new Partition
        {
            Number = (int)Wmi.Get<uint>(mo, "PartitionNumber"),
            OffsetBytes = Wmi.Get<ulong>(mo, "Offset"),
            SizeBytes = Wmi.Get<ulong>(mo, "Size"),
            Kind = kind,
            TypeDescription = description,
            DriveLetter = Wmi.ReadDriveLetter(mo) ?? volume?.DriveLetter,
            IsSystem = Wmi.Get<bool>(mo, "IsSystem"),
            IsBoot = Wmi.Get<bool>(mo, "IsBoot"),
            IsHidden = Wmi.Get<bool>(mo, "IsHidden"),
            IsActive = Wmi.Get<bool>(mo, "IsActive"),
            Volume = volume,
        };

        return (diskNumber, partition);
    }

    internal static Volume? ReadVolume(ManagementBaseObject mo)
    {
        var path = Wmi.Get<string>(mo, "Path");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var letter = Wmi.ReadDriveLetter(mo);

        // The WMI flag is cached; the live FSCTL answer wins whenever we can get it.
        var dirty = Wmi.Get<bool>(mo, "DirtyBitSet");
        if (letter is { } l && VolumeDirtyBit.Query(l) is { } live)
        {
            dirty = live;
        }

        return new Volume
        {
            Path = path,
            DriveLetter = letter,
            Label = Wmi.Get<string>(mo, "FileSystemLabel") is { Length: > 0 } label ? label : null,
            FileSystem = Wmi.Get<string>(mo, "FileSystem") ?? string.Empty,
            SizeBytes = Wmi.Get<ulong>(mo, "Size"),
            FreeBytes = Wmi.Get<ulong>(mo, "SizeRemaining"),
            Health = Wmi.Get<ushort>(mo, "HealthStatus") switch
            {
                0 => VolumeHealth.Healthy,
                1 => VolumeHealth.ScanNeeded,
                2 => VolumeHealth.SpotFixNeeded,
                3 => VolumeHealth.FullRepairNeeded,
                _ => VolumeHealth.Unknown,
            },
            IsDirty = dirty,
        };
    }
}
