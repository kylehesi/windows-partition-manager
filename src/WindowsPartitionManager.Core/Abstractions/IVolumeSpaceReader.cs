using WindowsPartitionManager.Core.Analysis;

namespace WindowsPartitionManager.Core.Abstractions;

/// <summary>Reads every file and folder of an NTFS volume with the space it takes, straight from the MFT. Read-only.</summary>
public interface IVolumeSpaceReader
{
    Task<IReadOnlyList<SpaceEntry>> ReadAsync(char driveLetter, IProgress<InspectProgress>? progress = null, CancellationToken cancellationToken = default);
}
