using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Virtual disks for the user: created, attached and detached through virtdisk.dll. Attached
/// disks get drive letters and stay attached until detached or until Windows restarts. Every
/// call re-checks the rules and is written to the operation log like any other change.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VirtualDiskOperations(IStorageOperations storage) : IVirtualDiskOperations
{
    public Task<int> CreateAsync(CreateVirtualDiskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Create(CultureInfo.InvariantCulture, $"file=\"{request.Path}\" size={request.SizeBytes} style={request.Style}");
        return Logged("CreateVirtualDisk", details, async () =>
        {
            var free = TryFreeBytes(request.Path);
            Refuse("CreateVirtualDisk", VirtualDiskPlanner.ValidateCreate(request, File.Exists(request.Path) || Directory.Exists(request.Path), free));

            int number;
            using (var vhd = VirtualDisk.Create(request.Path, request.SizeBytes))
            {
                try
                {
                    vhd.Attach(permanent: true, assignDriveLetters: true);
                    number = vhd.GetDiskNumber();
                }
                catch (Win32Exception)
                {
                    // Never leave a file behind that the user did not get a disk for.
                    vhd.Dispose();
                    File.Delete(request.Path);
                    throw;
                }
            }

            if (request.Style is PartitionStyle.Gpt or PartitionStyle.Mbr)
            {
                await WaitForDiskAsync(number, cancellationToken);
                await storage.InitializeDiskAsync(number, request.Style, cancellationToken);
            }

            return number;
        });
    }

    public Task<int> AttachAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Logged("AttachVirtualDisk", $"file=\"{path}\"", () =>
        {
            if (!File.Exists(path))
            {
                throw new StorageOperationException("AttachVirtualDisk", 0, CoreText.Format("Vhd_FileMissing", path));
            }

            using var vhd = VirtualDisk.Open(path);
            vhd.Attach(permanent: true, assignDriveLetters: true);
            return Task.FromResult(vhd.GetDiskNumber());
        });
    }

    public Task DetachAsync(DetachVirtualDiskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Logged("DetachVirtualDisk", string.Create(CultureInfo.InvariantCulture, $"disk={request.DiskNumber}"), () =>
        {
            var fresh = WmiStorageProvider.GetDisks(cancellationToken).FirstOrDefault(d => d.Number == request.DiskNumber);
            Refuse("DetachVirtualDisk", [LayoutGuard.Check(fresh, request.ExpectedDisk)]);
            Refuse("DetachVirtualDisk", VirtualDiskPlanner.ValidateDetach(fresh!, request));

            using var vhd = VirtualDisk.Open(fresh!.VirtualDiskPath!);
            vhd.Detach();
            return Task.FromResult(0);
        });
    }

    private static void Refuse(string operation, IEnumerable<ValidationIssue?> issues)
    {
        var errors = issues.Where(i => i is { IsError: true }).Select(i => i!.Message).ToList();
        if (errors.Count > 0)
        {
            throw new StorageOperationException(operation, 0, CoreText.Get("Ops_RefusedPrefix") + string.Join(" ", errors));
        }
    }

    /// <summary>Same contract as the partition operations: sleep held off, outcome logged, Win32 errors surfaced as storage errors.</summary>
    private static async Task<T> Logged<T>(string operation, string details, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        using var awake = KeepAwake.Begin();
        try
        {
            var result = await Task.Run(action);
            OperationLog.Append(operation, details, string.Create(CultureInfo.InvariantCulture, $"OK ({stopwatch.Elapsed.TotalSeconds:F1} s)"));
            return result;
        }
        catch (Win32Exception ex)
        {
            OperationLog.Append(operation, details, $"FAILED: {ex.Message}");
            throw new StorageOperationException(operation, (uint)ex.NativeErrorCode, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationLog.Append(operation, details, $"FAILED: {ex.Message}");
            throw;
        }
    }

    private static ulong? TryFreeBytes(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? (ulong)new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A freshly attached disk takes a moment to appear in the Storage API.</summary>
    private static async Task WaitForDiskAsync(int number, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!WmiStorageProvider.GetDisks(cancellationToken).Any(d => d.Number == number))
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new StorageOperationException("CreateVirtualDisk", 0, CoreText.Get("Vhd_DiskTimeout"));
            }

            await Task.Delay(500, cancellationToken);
        }
    }
}
