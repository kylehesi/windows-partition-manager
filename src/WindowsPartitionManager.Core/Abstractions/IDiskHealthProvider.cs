using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Abstractions;

/// <summary>Reads what a drive reports about its own health. Read-only; nothing is written.</summary>
public interface IDiskHealthProvider
{
    /// <summary>Returns null when Windows knows no physical disk behind the disk number.</summary>
    Task<DiskHealth?> GetHealthAsync(int diskNumber, CancellationToken cancellationToken = default);
}
