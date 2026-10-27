using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Abstractions;

/// <summary>
/// Virtual disks (VHDX files) that behave like real ones: a safe place to practise every
/// operation, or extra storage kept in a single file. Attached disks stay attached until they are
/// detached or Windows restarts.
/// </summary>
public interface IVirtualDiskOperations
{
    /// <summary>Creates a dynamically expanding VHDX, attaches it and, if asked, writes a partition table. Returns the disk number.</summary>
    Task<int> CreateAsync(CreateVirtualDiskRequest request, CancellationToken cancellationToken = default);

    /// <summary>Attaches an existing VHD or VHDX file. Returns the disk number.</summary>
    Task<int> AttachAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detaches a virtual disk the way Disk Management does: offline first (Windows flushes and
    /// dismounts its volumes), then removed. The file itself stays.
    /// </summary>
    Task DetachAsync(DetachVirtualDiskRequest request, CancellationToken cancellationToken = default);
}
