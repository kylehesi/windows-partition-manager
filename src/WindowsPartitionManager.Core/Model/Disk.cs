namespace WindowsPartitionManager.Core.Model;

/// <summary>Partition table style of a physical disk.</summary>
public enum PartitionStyle
{
    Unknown = 0,
    Mbr = 1,
    Gpt = 2,

    /// <summary>The disk has no partition table yet (shown as "Not Initialized" by Windows).</summary>
    Raw = 3,
}

public enum HealthStatus
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy,
}

/// <summary>A physical (or virtual) disk as seen by Windows, together with its partitions.</summary>
public sealed record Disk
{
    public required int Number { get; init; }

    public required string FriendlyName { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>Human readable bus type, e.g. "SATA", "NVMe", "USB".</summary>
    public required string BusType { get; init; }

    public required ulong SizeBytes { get; init; }

    public required uint LogicalSectorSize { get; init; }

    public required uint PhysicalSectorSize { get; init; }

    public required PartitionStyle Style { get; init; }

    /// <summary>True when the disk holds the EFI/system partition Windows boots from.</summary>
    public bool IsSystem { get; init; }

    /// <summary>True when the running Windows installation lives on this disk.</summary>
    public bool IsBoot { get; init; }

    public bool IsReadOnly { get; init; }

    public bool IsOffline { get; init; }

    public HealthStatus Health { get; init; } = HealthStatus.Unknown;

    /// <summary>Size of the largest unallocated region as Windows itself computes it; a cross-check for our own layout math.</summary>
    public ulong LargestFreeExtentBytes { get; init; }

    /// <summary>The VHD or VHDX file behind the disk when it is an attached virtual disk, otherwise null.</summary>
    public string? VirtualDiskPath { get; init; }

    /// <summary>Partitions ordered by their offset on the disk.</summary>
    public required IReadOnlyList<Partition> Partitions { get; init; }
}
