using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Formatting;

/// <summary>
/// Machine-readable disk listing for scripts (<c>wpm list --json</c>). Sizes are bytes, names are
/// camelCase, and <c>schemaVersion</c> changes only when a field is removed or changes meaning.
/// </summary>
public static class DiskJson
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    public static string Serialize(IReadOnlyList<Disk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        return JsonSerializer.Serialize(new DiskListing(SchemaVersion, disks.Select(ToJson).ToList()), Options);
    }

    private static DiskEntry ToJson(Disk disk) => new(
        disk.Number,
        disk.FriendlyName,
        disk.SerialNumber,
        disk.BusType,
        disk.SizeBytes,
        disk.LogicalSectorSize,
        disk.PhysicalSectorSize,
        disk.Style,
        disk.Health,
        disk.IsSystem,
        disk.IsBoot,
        disk.IsReadOnly,
        disk.IsOffline,
        disk.Partitions.Select(p => new PartitionEntry(
            p.Number,
            p.OffsetBytes,
            p.SizeBytes,
            p.Kind,
            p.TypeDescription,
            p.DriveLetter?.ToString(),
            p.IsSystem,
            p.IsBoot,
            p.IsHidden,
            p.Volume is { } v ? new VolumeEntry(v.FileSystem, v.Label, v.SizeBytes, v.FreeBytes, v.UsedBytes, v.Health, v.IsDirty, v.BitLocker is { } b ? new BitLockerEntry(b.State, b.ProtectionOn, b.Locked, b.PercentEncrypted) : null) : null)).ToList(),
        FreeSpaceCalculator.Compute(disk).Select(r => new FreeEntry(r.OffsetBytes, r.SizeBytes)).ToList());

    private sealed record DiskListing(int SchemaVersion, IReadOnlyList<DiskEntry> Disks);

    private sealed record DiskEntry(
        int Number,
        string FriendlyName,
        string? SerialNumber,
        string BusType,
        ulong SizeBytes,
        uint LogicalSectorSize,
        uint PhysicalSectorSize,
        PartitionStyle Style,
        HealthStatus Health,
        bool IsSystem,
        bool IsBoot,
        bool IsReadOnly,
        bool IsOffline,
        IReadOnlyList<PartitionEntry> Partitions,
        IReadOnlyList<FreeEntry> FreeRegions);

    private sealed record PartitionEntry(
        int Number,
        ulong OffsetBytes,
        ulong SizeBytes,
        PartitionKind Kind,
        string Type,
        string? DriveLetter,
        bool IsSystem,
        bool IsBoot,
        bool IsHidden,
        VolumeEntry? Volume);

    private sealed record VolumeEntry(string FileSystem, string? Label, ulong SizeBytes, ulong FreeBytes, ulong UsedBytes, VolumeHealth Health, bool IsDirty, BitLockerEntry? BitLocker);

    /// <summary>Added in schema 1 without a version bump: a new, optional field.</summary>
    private sealed record BitLockerEntry(EncryptionState State, bool ProtectionOn, bool Locked, int? PercentEncrypted);

    private sealed record FreeEntry(ulong OffsetBytes, ulong SizeBytes);
}
