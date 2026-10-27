using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Partition table writes through the Storage Management API: the same calls PowerShell's
/// Initialize-Disk, New-Partition, Format-Volume, Resize-Partition and Remove-Partition make.
/// Windows Partition Manager never writes raw sectors; every change goes through Windows so that
/// NTFS journaling and GPT redundancy keep a power loss recoverable. Requires elevation.
/// <para>
/// Every operation re-reads the disk immediately before writing, checks that the disk and
/// partition are still the ones the caller saw (fingerprints), and runs the same planner rules
/// the UI ran. A caller that skipped validation, or a screen that went stale, is refused here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiStorageOperations : IStorageOperations
{
    private const string BasicDataGptType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    private const ushort BasicDataMbrType = 0x07;
    private static readonly TimeSpan VolumeWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly PowerStatusProvider Power = new();

    public Task InitializeDiskAsync(int diskNumber, PartitionStyle style, CancellationToken cancellationToken = default)
        => Task.Run(() => Guarded("Initialize", $"disk={diskNumber} style={style}", () => InitializeDisk(diskNumber, style)), cancellationToken);

    public Task<Partition> CreatePartitionAsync(CreatePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Create(CultureInfo.InvariantCulture, $"disk={request.DiskNumber} offset={request.OffsetBytes} size={request.SizeBytes} fs={request.FileSystem} letter={request.DriveLetter?.ToString() ?? "auto"}");
        return Task.Run(() => Guarded("CreatePartition", details, () => CreatePartition(request, progress, cancellationToken)), cancellationToken);
    }

    public Task<Partition> FormatPartitionAsync(FormatPartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Create(CultureInfo.InvariantCulture, $"disk={request.DiskNumber} partition={request.PartitionNumber} fs={request.FileSystem} quick={request.QuickFormat}");
        return Task.Run(() => Guarded("Format", details, () => FormatPartition(request, progress, cancellationToken)), cancellationToken);
    }

    public Task DeletePartitionAsync(DeletePartitionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Create(CultureInfo.InvariantCulture, $"disk={request.DiskNumber} partition={request.PartitionNumber}");
        return Task.Run(() => Guarded("DeletePartition", details, () => DeletePartition(request, cancellationToken)), cancellationToken);
    }

    public Task<Partition> ResizePartitionAsync(ResizePartitionRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Create(CultureInfo.InvariantCulture, $"disk={request.DiskNumber} partition={request.PartitionNumber} newSize={request.NewSizeBytes}");
        return Task.Run(() => Guarded("Resize", details, () => ResizePartition(request, progress, cancellationToken)), cancellationToken);
    }

    public Task<Partition> ChangeDriveLetterAsync(ChangeDriveLetterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Create(CultureInfo.InvariantCulture, $"disk={request.DiskNumber} partition={request.PartitionNumber} letter={request.NewDriveLetter?.ToString() ?? "none"}");
        return Task.Run(() => Guarded("ChangeDriveLetter", details, () => ChangeDriveLetter(request, cancellationToken)), cancellationToken);
    }

    public Task<Partition> SetLabelAsync(SetLabelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Create(CultureInfo.InvariantCulture, $"disk={request.DiskNumber} partition={request.PartitionNumber} label=\"{request.Label}\"");
        return Task.Run(() => Guarded("SetLabel", details, () => SetLabel(request, cancellationToken)), cancellationToken);
    }

    public Task<Disk> SetDiskStateAsync(SetDiskStateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Create(CultureInfo.InvariantCulture, $"disk={request.DiskNumber} change={request.Change}");
        return Task.Run(() => Guarded("SetDiskState", details, () => SetDiskState(request, cancellationToken)), cancellationToken);
    }

    /// <summary>Runs a write operation with the sleep inhibitor held and the outcome logged.</summary>
    private static T Guarded<T>(string operation, string details, Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        using var awake = KeepAwake.Begin();
        try
        {
            var result = action();
            OperationLog.Append(operation, details, string.Create(CultureInfo.InvariantCulture, $"OK ({stopwatch.Elapsed.TotalSeconds:F1} s)"));
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationLog.Append(operation, details, $"FAILED: {ex.Message}");
            throw;
        }
    }

    private static void Guarded(string operation, string details, Action action)
        => Guarded(operation, details, () =>
        {
            action();
            return true;
        });

    /// <summary>Throws, before anything is written, when any rule reports an error.</summary>
    private static void RefuseOnErrors(string operation, IEnumerable<ValidationIssue?> issues)
    {
        var errors = issues.Where(i => i is { IsError: true }).Select(i => i!.Message).ToList();
        if (errors.Count > 0)
        {
            throw new StorageOperationException(operation, 0, CoreText.Get("Ops_RefusedPrefix") + string.Join(" ", errors));
        }
    }

    private static (List<Disk> Disks, Disk? Disk) Fresh(int diskNumber, CancellationToken cancellationToken)
    {
        var disks = WmiStorageProvider.GetDisks(cancellationToken);
        return (disks, disks.FirstOrDefault(d => d.Number == diskNumber));
    }

    private static HashSet<char> LettersInUse(IEnumerable<Disk> disks)
    {
        var letters = new HashSet<char>(PartitionPlanner.LettersInUse(disks));
        foreach (var root in Environment.GetLogicalDrives())
        {
            if (root.Length > 0 && char.IsLetter(root[0]))
            {
                letters.Add(char.ToUpperInvariant(root[0]));
            }
        }

        return letters;
    }

    private static void InitializeDisk(int diskNumber, PartitionStyle style)
    {
        if (style is not (PartitionStyle.Gpt or PartitionStyle.Mbr))
        {
            throw new ArgumentOutOfRangeException(nameof(style), "Choose GPT or MBR.");
        }

        var scope = Wmi.Connect();
        using var disk = GetDisk(scope, diskNumber);

        if (Wmi.Get<bool>(disk, "IsOffline"))
        {
            throw new StorageOperationException("Initialize", 41003, CoreText.Get("Ops_InitializeOffline"));
        }

        if (Wmi.Get<ushort>(disk, "PartitionStyle") != 0)
        {
            throw new StorageOperationException("Initialize", 41001, CoreText.Get("Ops_AlreadyHasTable"));
        }

        Wmi.Invoke(disk, "Initialize", p => p["PartitionStyle"] = (ushort)(style == PartitionStyle.Gpt ? 2 : 1)).Dispose();
    }

    private static Partition CreatePartition(CreatePartitionRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var (disks, fresh) = Fresh(request.DiskNumber, cancellationToken);
        RefuseOnErrors("CreatePartition", [LayoutGuard.Check(fresh, request.ExpectedDisk)]);
        RefuseOnErrors("CreatePartition", PartitionPlanner.Validate(fresh!, request, LettersInUse(disks)));
        RefuseOnErrors("CreatePartition", SafetyChecks.Power(Power.GetPowerStatus(), touchesSystemVolume: false));

        var scope = Wmi.Connect();
        using var disk = GetDisk(scope, request.DiskNumber);
        var isGpt = Wmi.Get<ushort>(disk, "PartitionStyle") == 2;

        progress?.Report(CoreText.Get("Ops_CreatingPartition"));
        uint partitionNumber;
        string[] accessPaths;
        using (var result = Wmi.Invoke(disk, "CreatePartition", p =>
        {
            p["Offset"] = request.OffsetBytes;
            p["Size"] = request.SizeBytes;
            p["UseMaximumSize"] = false;
            p["AssignDriveLetter"] = false; // assigned after the format so the shell never sees an unformatted drive
            if (isGpt)
            {
                p["GptType"] = BasicDataGptType;
            }
            else
            {
                p["MbrType"] = BasicDataMbrType;
            }
        }))
        {
            if (Wmi.GetRaw(result, "CreatedPartition") is not ManagementBaseObject created)
            {
                throw new StorageOperationException("CreatePartition", 0, CoreText.Get("Ops_NoPartitionReturned"));
            }

            using (created)
            {
                partitionNumber = Wmi.Get<uint>(created, "PartitionNumber");
                accessPaths = Wmi.GetRaw(created, "AccessPaths") as string[] ?? [];
            }
        }

        // Up to here nothing existing was touched. If the new partition cannot be formatted,
        // remove it again so the user is not left with an unexplained empty partition.
        try
        {
            progress?.Report(CoreText.Get("Ops_WaitingForVolume"));
            using var volume = WaitForVolume(scope, request.DiskNumber, (int)partitionNumber, accessPaths, cancellationToken);

            progress?.Report($"Formatting {request.FileSystem}\u2026");
            FormatVolume(volume, request.FileSystem, request.Label, request.QuickFormat);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var cleanup = TryDeleteJustCreated(scope, request, (int)partitionNumber)
                ? CoreText.Get("Ops_CleanedUp")
                : CoreText.Format("Ops_CleanupFailed", partitionNumber);
            throw new StorageOperationException("CreatePartition", (ex as StorageOperationException)?.ReturnValue ?? 0, $"{ex.Message} {cleanup}");
        }

        progress?.Report(CoreText.Get("Ops_AssigningLetter"));
        AssignLetter(scope, request, (int)partitionNumber, progress);

        progress?.Report(CoreText.Get("Ops_ReadingBack"));
        return ReadBack(request.DiskNumber, (int)partitionNumber, "CreatePartition", cancellationToken);
    }

    /// <summary>A chosen letter that turns out to be taken falls back to the next free one instead of failing a finished partition.</summary>
    private static void AssignLetter(ManagementScope scope, CreatePartitionRequest request, int partitionNumber, IProgress<string>? progress)
    {
        using var partition = GetPartition(scope, request.DiskNumber, partitionNumber);
        if (request.DriveLetter is { } letter)
        {
            try
            {
                // AccessPath and AssignDriveLetter are mutually exclusive; passing both (even false) is "Invalid parameter".
                Wmi.Invoke(partition, "AddAccessPath", p => p["AccessPath"] = $"{char.ToUpperInvariant(letter)}:").Dispose();
                return;
            }
            catch (StorageOperationException ex)
            {
                progress?.Report(CoreText.Format("Ops_LetterFallback", letter, ex.Message));
            }
        }

        try
        {
            Wmi.Invoke(partition, "AddAccessPath", p => p["AssignDriveLetter"] = true).Dispose();
        }
        catch (StorageOperationException ex)
        {
            progress?.Report(CoreText.Format("Ops_NoLetter", ex.Message));
        }
    }

    /// <summary>Deletes the partition only if it is still exactly the one this call created.</summary>
    private static bool TryDeleteJustCreated(ManagementScope scope, CreatePartitionRequest request, int partitionNumber)
    {
        try
        {
            using var partition = GetPartition(scope, request.DiskNumber, partitionNumber);
            if (Wmi.Get<ulong>(partition, "Offset") != request.OffsetBytes)
            {
                return false;
            }

            Wmi.Invoke(partition, "DeleteObject").Dispose();
            OperationLog.Append("DeletePartition", $"disk={request.DiskNumber} partition={partitionNumber}", "OK (cleanup after failed create)");
            return true;
        }
        catch (Exception ex) when (ex is StorageOperationException or ManagementException)
        {
            OperationLog.Append("DeletePartition", $"disk={request.DiskNumber} partition={partitionNumber}", $"FAILED (cleanup after failed create): {ex.Message}");
            return false;
        }
    }

    private static Partition FormatPartition(FormatPartitionRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var (_, fresh) = Fresh(request.DiskNumber, cancellationToken);
        var target = fresh?.Partitions.FirstOrDefault(p => p.Number == request.PartitionNumber);
        RefuseOnErrors("Format", [LayoutGuard.Check(fresh, request.ExpectedDisk, target, request.ExpectedPartition)]);
        if (target is null)
        {
            throw new StorageOperationException("Format", 0, CoreText.Get("Ops_RefusedPrefix") + CoreText.Format("Ops_PartitionNotFound", request.PartitionNumber, request.DiskNumber, LayoutGuard.RefreshHint));
        }

        RefuseOnErrors("Format", FormatPlanner.Validate(fresh!, target, request));
        RefuseOnErrors("Format", SafetyChecks.Power(Power.GetPowerStatus(), touchesSystemVolume: false));

        var scope = Wmi.Connect();
        string[] accessPaths;
        using (var partition = GetPartition(scope, request.DiskNumber, request.PartitionNumber))
        {
            accessPaths = Wmi.GetRaw(partition, "AccessPaths") as string[] ?? [];
        }

        progress?.Report(CoreText.Get("Ops_LocatingVolume"));
        using var volume = WaitForVolume(scope, request.DiskNumber, request.PartitionNumber, accessPaths, cancellationToken);

        progress?.Report($"Formatting {request.FileSystem}\u2026");
        FormatVolume(volume, request.FileSystem, request.Label, request.QuickFormat);

        progress?.Report(CoreText.Get("Ops_ReadingBack"));
        return ReadBack(request.DiskNumber, request.PartitionNumber, "Format", cancellationToken);
    }

    private static void FormatVolume(ManagementObject volume, string fileSystem, string label, bool quick)
    {
        Wmi.Invoke(volume, "Format", p =>
        {
            p["FileSystem"] = fileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase) ? "exFAT" : fileSystem.ToUpperInvariant();
            p["FileSystemLabel"] = label;
            p["Full"] = !quick;
            p["Force"] = true;
        }).Dispose();
    }

    private static void DeletePartition(DeletePartitionRequest request, CancellationToken cancellationToken)
    {
        var (_, fresh) = Fresh(request.DiskNumber, cancellationToken);
        var target = fresh?.Partitions.FirstOrDefault(p => p.Number == request.PartitionNumber);
        RefuseOnErrors("DeletePartition", [LayoutGuard.Check(fresh, request.ExpectedDisk, target, request.ExpectedPartition)]);
        if (target is null)
        {
            throw new StorageOperationException("DeletePartition", 0, CoreText.Get("Ops_RefusedPrefix") + CoreText.Format("Ops_PartitionNotFound", request.PartitionNumber, request.DiskNumber, LayoutGuard.RefreshHint));
        }

        RefuseOnErrors("DeletePartition", PartitionPlanner.ValidateDelete(fresh!, target));
        RefuseOnErrors("DeletePartition", SafetyChecks.Power(Power.GetPowerStatus(), touchesSystemVolume: false));

        var scope = Wmi.Connect();
        using var partition = GetPartition(scope, request.DiskNumber, request.PartitionNumber);
        Wmi.Invoke(partition, "DeleteObject").Dispose();
    }

    private static Partition ResizePartition(ResizePartitionRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var (_, fresh) = Fresh(request.DiskNumber, cancellationToken);
        var before = fresh?.Partitions.FirstOrDefault(p => p.Number == request.PartitionNumber);
        RefuseOnErrors("Resize", [LayoutGuard.Check(fresh, request.ExpectedDisk, before, request.ExpectedPartition)]);
        if (before is null)
        {
            throw new StorageOperationException("Resize", 0, CoreText.Get("Ops_RefusedPrefix") + CoreText.Format("Ops_PartitionNotFound", request.PartitionNumber, request.DiskNumber, LayoutGuard.RefreshHint));
        }

        // Never resize a volume Windows already considers damaged; checked first so the message is specific.
        if (before.Volume is { } volume)
        {
            RefuseOnErrors("Resize", SafetyChecks.VolumeHealth(volume));
        }

        RefuseOnErrors("Resize", ResizePlanner.Validate(fresh!, before, request));
        RefuseOnErrors("Resize", SafetyChecks.Power(Power.GetPowerStatus(), touchesSystemVolume: before.IsBoot || before.IsSystem));

        var scope = Wmi.Connect();
        using (var partition = GetPartition(scope, request.DiskNumber, request.PartitionNumber))
        {
            var current = Wmi.Get<ulong>(partition, "Size");
            progress?.Report(request.NewSizeBytes < current
                ? CoreText.Get("Ops_Shrinking")
                : CoreText.Get("Ops_Extending"));

            Wmi.Invoke(partition, "Resize", p => p["Size"] = request.NewSizeBytes).Dispose();
        }

        progress?.Report(CoreText.Get("Ops_ReadingBack"));
        return ReadBack(request.DiskNumber, request.PartitionNumber, "Resize", cancellationToken);
    }

    private static Partition ChangeDriveLetter(ChangeDriveLetterRequest request, CancellationToken cancellationToken)
    {
        var (disks, fresh) = Fresh(request.DiskNumber, cancellationToken);
        var target = fresh?.Partitions.FirstOrDefault(p => p.Number == request.PartitionNumber);
        RefuseOnErrors("ChangeDriveLetter", [LayoutGuard.Check(fresh, request.ExpectedDisk, target, request.ExpectedPartition)]);
        if (target is null)
        {
            throw new StorageOperationException("ChangeDriveLetter", 0, CoreText.Get("Ops_RefusedPrefix") + CoreText.Format("Ops_PartitionNotFound", request.PartitionNumber, request.DiskNumber, LayoutGuard.RefreshHint));
        }

        RefuseOnErrors("ChangeDriveLetter", DriveLetterPlanner.Validate(fresh!, target, request, LettersInUse(disks)));

        var scope = Wmi.Connect();
        using var partition = GetPartition(scope, request.DiskNumber, request.PartitionNumber);
        var oldPaths = (Wmi.GetRaw(partition, "AccessPaths") as string[] ?? [])
            .Where(p => p.Length == 3 && char.IsLetter(p[0]) && p.EndsWith(@":\", StringComparison.Ordinal))
            .ToList();

        // A volume holds at most one drive letter (AddAccessPath returns 42012 otherwise), so the
        // old letter goes first; if the new one is then refused, the old one is put back.
        foreach (var path in oldPaths)
        {
            Wmi.Invoke(partition, "RemoveAccessPath", p => p["AccessPath"] = path).Dispose();
        }

        if (request.NewDriveLetter is { } letter)
        {
            try
            {
                Wmi.Invoke(partition, "AddAccessPath", p => p["AccessPath"] = $"{char.ToUpperInvariant(letter)}:").Dispose();
            }
            catch (StorageOperationException) when (oldPaths.Count > 0)
            {
                Wmi.Invoke(partition, "AddAccessPath", p => p["AccessPath"] = oldPaths[0][..2]).Dispose();
                throw;
            }
        }

        return ReadBack(request.DiskNumber, request.PartitionNumber, "ChangeDriveLetter", cancellationToken);
    }

    private static Partition SetLabel(SetLabelRequest request, CancellationToken cancellationToken)
    {
        var (_, fresh) = Fresh(request.DiskNumber, cancellationToken);
        var target = fresh?.Partitions.FirstOrDefault(p => p.Number == request.PartitionNumber);
        RefuseOnErrors("SetLabel", [LayoutGuard.Check(fresh, request.ExpectedDisk, target, request.ExpectedPartition)]);
        if (target is null)
        {
            throw new StorageOperationException("SetLabel", 0, CoreText.Get("Ops_RefusedPrefix") + CoreText.Format("Ops_PartitionNotFound", request.PartitionNumber, request.DiskNumber, LayoutGuard.RefreshHint));
        }

        RefuseOnErrors("SetLabel", LabelPlanner.Validate(fresh!, target, request));

        var scope = Wmi.Connect();
        string[] accessPaths;
        using (var partition = GetPartition(scope, request.DiskNumber, request.PartitionNumber))
        {
            accessPaths = Wmi.GetRaw(partition, "AccessPaths") as string[] ?? [];
        }

        using (var volume = WaitForVolume(scope, request.DiskNumber, request.PartitionNumber, accessPaths, cancellationToken))
        {
            Wmi.Invoke(volume, "SetFileSystemLabel", p => p["FileSystemLabel"] = request.Label).Dispose();
        }

        return ReadBack(request.DiskNumber, request.PartitionNumber, "SetLabel", cancellationToken);
    }

    private static Disk SetDiskState(SetDiskStateRequest request, CancellationToken cancellationToken)
    {
        var (_, fresh) = Fresh(request.DiskNumber, cancellationToken);
        RefuseOnErrors("SetDiskState", [LayoutGuard.Check(fresh, request.ExpectedDisk)]);
        RefuseOnErrors("SetDiskState", DiskStatePlanner.Validate(fresh!, request));

        var scope = Wmi.Connect();
        using (var disk = GetDisk(scope, request.DiskNumber))
        {
            switch (request.Change)
            {
                case DiskStateChange.Online:
                    Wmi.Invoke(disk, "Online").Dispose();
                    break;
                case DiskStateChange.Offline:
                    Wmi.Invoke(disk, "Offline").Dispose();
                    break;
                case DiskStateChange.ReadOnly or DiskStateChange.Writable:
                    Wmi.Invoke(disk, "SetAttributes", p => p["IsReadOnly"] = request.Change == DiskStateChange.ReadOnly).Dispose();
                    break;
            }
        }

        return WmiStorageProvider.GetDisks(cancellationToken).FirstOrDefault(d => d.Number == request.DiskNumber)
               ?? throw new StorageOperationException("SetDiskState", 0, $"Disk {request.DiskNumber} could not be read.");
    }

    private static Partition ReadBack(int diskNumber, int partitionNumber, string operation, CancellationToken cancellationToken)
    {
        var disks = WmiStorageProvider.GetDisks(cancellationToken);
        return disks.FirstOrDefault(d => d.Number == diskNumber)?.Partitions.FirstOrDefault(p => p.Number == partitionNumber)
            ?? throw new StorageOperationException(operation, 0, $"Partition {partitionNumber} on disk {diskNumber} could not be read.");
    }

    private static ManagementObject GetDisk(ManagementScope scope, int diskNumber)
        => Wmi.QuerySingle(scope, $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}")
           ?? throw new StorageOperationException("GetDisk", 0, $"Disk {diskNumber} was not found.");

    private static ManagementObject GetPartition(ManagementScope scope, int diskNumber, int partitionNumber)
        => Wmi.QuerySingle(scope, $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}")
           ?? throw new StorageOperationException("GetPartition", 0, $"Partition {partitionNumber} on disk {diskNumber} was not found.");

    /// <summary>The mount manager registers the new volume asynchronously; poll until it shows up.</summary>
    private static ManagementObject WaitForVolume(ManagementScope scope, int diskNumber, int partitionNumber, string[] accessPaths, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (accessPaths.Length == 0)
            {
                using var partition = GetPartition(scope, diskNumber, partitionNumber);
                accessPaths = Wmi.GetRaw(partition, "AccessPaths") as string[] ?? [];
            }

            foreach (var path in accessPaths)
            {
                if (!path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var escaped = path.Replace(@"\", @"\\", StringComparison.Ordinal);
                var volume = Wmi.QuerySingle(scope, $"SELECT * FROM MSFT_Volume WHERE Path = '{escaped}'");
                if (volume is not null)
                {
                    return volume;
                }
            }

            if (stopwatch.Elapsed > VolumeWaitTimeout)
            {
                throw new StorageOperationException("CreatePartition", 0, CoreText.Get("Ops_VolumeTimeout"));
            }

            accessPaths = [];
            Thread.Sleep(500);
        }
    }
}
