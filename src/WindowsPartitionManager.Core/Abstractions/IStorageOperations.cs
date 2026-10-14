using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Abstractions;

/// <summary>Writes to the partition table. Every method changes the disk; validate first.</summary>
public interface IStorageOperations
{
    /// <summary>Writes a partition table to a RAW disk.</summary>
    Task InitializeDiskAsync(int diskNumber, PartitionStyle style, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the partition, waits for its volume to appear, formats it and assigns the drive letter.
    /// Returns the resulting partition as Windows reports it afterwards.
    /// </summary>
    Task<Partition> CreatePartitionAsync(CreatePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Reformats an existing partition in place and keeps its drive letter. Irreversible.</summary>
    Task<Partition> FormatPartitionAsync(FormatPartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a partition and everything on it. Irreversible.</summary>
    Task DeletePartitionAsync(DeletePartitionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shrinks or extends a partition together with its file system (Storage API Resize, the same
    /// call as Resize-Partition). Windows relocates movable files itself; it refuses to shrink past
    /// unmovable ones. Returns the partition as Windows reports it afterwards.
    /// </summary>
    Task<Partition> ResizePartitionAsync(ResizePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Reads whether the machine is on battery, so write operations can refuse or warn.</summary>
public interface IPowerStatusProvider
{
    PowerStatus? GetPowerStatus();
}

/// <summary>Raised when the Storage Management API refuses an operation.</summary>
public sealed class StorageOperationException(string operation, uint returnValue, string message)
    : Exception(CoreText.Format("Ops_Failed", operation, returnValue, message))
{
    public string Operation { get; } = operation;

    public uint ReturnValue { get; } = returnValue;
}

public static class StorageOperationsExtensions
{
    /// <summary>
    /// Delete without fingerprints, for tests and scripts that have just created the partition
    /// themselves. The operations layer still re-reads the disk and applies the delete rules.
    /// </summary>
    public static Task DeletePartitionAsync(this IStorageOperations operations, int diskNumber, int partitionNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations.DeletePartitionAsync(new DeletePartitionRequest { DiskNumber = diskNumber, PartitionNumber = partitionNumber }, cancellationToken);
    }
}
