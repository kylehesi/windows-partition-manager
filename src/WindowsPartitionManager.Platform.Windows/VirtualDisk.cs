using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WindowsPartitionManager.Platform.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Creates, attaches and detaches VHDX files through virtdisk.dll (built into Windows 8 and later,
/// no Hyper-V needed). Used to test partition operations on throwaway disks instead of real ones.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class VirtualDisk : IDisposable
{
    private const uint VIRTUAL_STORAGE_TYPE_DEVICE_UNKNOWN = 0;
    private const uint VIRTUAL_STORAGE_TYPE_DEVICE_VHDX = 3;
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    private const uint VIRTUAL_DISK_ACCESS_NONE = 0;
    private const uint VIRTUAL_DISK_ACCESS_ALL = 0x003F0000;
    private const uint ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER = 0x2;
    private const uint ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME = 0x4;
    private const int ERROR_SUCCESS = 0;

    private readonly SafeFileHandle _handle;

    private VirtualDisk(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
    }

    public string Path { get; }

    /// <summary>Creates a dynamically expanding VHDX of the given size. The file must not exist yet.</summary>
    public static VirtualDisk Create(string path, ulong sizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            throw new IOException($"'{path}' already exists; choose another file name or detach and delete it first.");
        }

        // CreateVirtualDisk does not create missing folders; it fails with ERROR_PATH_NOT_FOUND instead.
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        var storageType = new VIRTUAL_STORAGE_TYPE { DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_VHDX, VendorId = MicrosoftVendor };
        var parameters = new CREATE_VIRTUAL_DISK_PARAMETERS_V2
        {
            Version = 2,
            MaximumSize = sizeBytes,
            BlockSizeInBytes = 0,            // default
            SectorSizeInBytes = 512,
            PhysicalSectorSizeInBytes = 4096,
        };

        var error = CreateVirtualDisk(in storageType, path, VIRTUAL_DISK_ACCESS_NONE, nint.Zero, 0, 0, in parameters, nint.Zero, out var handle);
        if (error != ERROR_SUCCESS)
        {
            handle?.Dispose();
            throw Failure(error, $"CreateVirtualDisk failed for '{path}'");
        }

        return new VirtualDisk(path, handle);
    }

    /// <summary>Opens an existing VHD or VHDX; the format is taken from the file extension.</summary>
    public static VirtualDisk Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var storageType = new VIRTUAL_STORAGE_TYPE { DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_UNKNOWN, VendorId = Guid.Empty };
        var error = OpenVirtualDisk(in storageType, path, VIRTUAL_DISK_ACCESS_ALL, 0, nint.Zero, out var handle);
        if (error != ERROR_SUCCESS)
        {
            handle?.Dispose();
            throw Failure(error, $"OpenVirtualDisk failed for '{path}'");
        }

        return new VirtualDisk(path, handle);
    }

    /// <summary>
    /// Surfaces the VHDX as a physical disk. Tests attach without drive letters so nothing on a
    /// throwaway disk shows up in Explorer; a disk the user attaches gets its letters as usual.
    /// With <paramref name="permanent"/> the disk stays attached after this process exits; otherwise
    /// it detaches when the handle is closed.
    /// </summary>
    public void Attach(bool permanent = false, bool assignDriveLetters = false)
    {
        // A permanent attach needs SeManageVolumePrivilege *enabled*; administrators hold it disabled by default.
        Privileges.TryEnable(Privileges.SeManageVolumePrivilege);

        var flags = (assignDriveLetters ? 0 : ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER) | (permanent ? ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME : 0);
        var parameters = new ATTACH_VIRTUAL_DISK_PARAMETERS { Version = 1 };

        var error = AttachVirtualDisk(_handle, nint.Zero, flags, 0, in parameters, nint.Zero);
        if (error != ERROR_SUCCESS)
        {
            throw Failure(error, "AttachVirtualDisk failed");
        }

        // A VHDX that was taken offline before an earlier detach comes back offline; bring it back.
        SetOnline(online: true);
    }

    /// <summary>
    /// Detaches the way Disk Management does: the disk is first taken offline, which makes Windows
    /// flush and dismount every volume on it, then removed, and the call returns only once the
    /// disk has disappeared. Pulling a disk out from under mounted volumes instead loses cached
    /// writes ("error during a paging operation") and, together with an immediate re-attach, has
    /// been seen to destabilise the kernel.
    /// </summary>
    public void Detach()
    {
        int? number = null;
        try
        {
            number = GetDiskNumber();
        }
        catch (Win32Exception)
        {
            // Not attached (any more); nothing to take offline.
        }

        if (number is not null)
        {
            SetOnline(online: false);
        }

        DetachWithoutDismount();

        if (number is { } n)
        {
            WaitUntilGone(n, TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// Surprise removal with volumes still mounted: what a pulled cable or a crash looks like.
    /// Only for deliberate crash-consistency tests; everything else must use <see cref="Detach"/>.
    /// </summary>
    public void DetachWithoutDismount()
    {
        Privileges.TryEnable(Privileges.SeManageVolumePrivilege);
        var error = DetachVirtualDisk(_handle, 0, 0);
        if (error != ERROR_SUCCESS)
        {
            throw Failure(error, "DetachVirtualDisk failed");
        }
    }

    /// <summary>Takes this VHDX's disk offline or online; refuses to touch anything that is not a virtual disk.</summary>
    private void SetOnline(bool online)
    {
        int number;
        try
        {
            number = GetDiskNumber();
        }
        catch (Win32Exception)
        {
            return;
        }

        try
        {
            var scope = Wmi.Connect();
            using var disk = Wmi.QuerySingle(scope, $"SELECT * FROM MSFT_Disk WHERE Number = {number}");
            if (disk is null)
            {
                return;
            }

            // 14 = Virtual, 15 = File-backed virtual. A real disk must never be taken offline here.
            var bus = Wmi.Get<ushort>(disk, "BusType");
            if (bus is not (14 or 15) || Wmi.Get<bool>(disk, "IsSystem") || Wmi.Get<bool>(disk, "IsBoot"))
            {
                return;
            }

            var isOffline = Wmi.Get<bool>(disk, "IsOffline");
            if (online && isOffline)
            {
                Wmi.Invoke(disk, "Online").Dispose();
            }
            else if (!online && !isOffline)
            {
                Wmi.Invoke(disk, "Offline").Dispose();
            }
        }
        catch (Exception ex) when (ex is Core.Abstractions.StorageOperationException or System.Management.ManagementException)
        {
            // Best effort: the detach itself still works, just less gently.
        }
    }

    private static void WaitUntilGone(int diskNumber, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var scope = Wmi.Connect();
                using var disk = Wmi.QuerySingle(scope, $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}");
                if (disk is null || Wmi.Get<ushort>(disk, "BusType") is not (14 or 15))
                {
                    break;
                }
            }
            catch (System.Management.ManagementException)
            {
                break;
            }

            Thread.Sleep(250);
        }

        // Let device-removal notifications (Explorer, Defender, every top-level window) finish
        // before anyone attaches the next disk.
        Thread.Sleep(2000);
    }

    /// <summary>The Windows disk number of the attached VHDX (parsed from \\.\PhysicalDriveN).</summary>
    public int GetDiskNumber()
    {
        var size = 1024u;
        var buffer = new char[size / 2];
        fixed (char* p = buffer)
        {
            var error = GetVirtualDiskPhysicalPath(_handle, ref size, p);
            if (error != ERROR_SUCCESS)
            {
                throw Failure(error, "GetVirtualDiskPhysicalPath failed");
            }
        }

        var path = new string(buffer).TrimEnd('\0');
        var digits = path.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray();
        if (digits.Length == 0)
        {
            throw new InvalidOperationException($"Unexpected physical path '{path}'.");
        }

        return int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Win32Exception with the system's own description of the error code appended, e.g. "The system cannot find the path specified".</summary>
    private static Win32Exception Failure(int error, string what)
        => new(error, $"{what}: {new Win32Exception(error).Message} (Win32 error {error})");

    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct VIRTUAL_STORAGE_TYPE
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATE_VIRTUAL_DISK_PARAMETERS_V2
    {
        public uint Version;
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        public uint PhysicalSectorSizeInBytes;
        public nint ParentPath;
        public nint SourcePath;
        public uint OpenFlags;
        public VIRTUAL_STORAGE_TYPE ParentVirtualStorageType;
        public VIRTUAL_STORAGE_TYPE SourceVirtualStorageType;
        public Guid ResiliencyGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ATTACH_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;
        public uint Reserved;
    }

    [LibraryImport("virtdisk.dll", EntryPoint = "CreateVirtualDisk", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateVirtualDisk(
        in VIRTUAL_STORAGE_TYPE virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        nint securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        in CREATE_VIRTUAL_DISK_PARAMETERS_V2 parameters,
        nint overlapped,
        out SafeFileHandle handle);

    [LibraryImport("virtdisk.dll", EntryPoint = "OpenVirtualDisk", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int OpenVirtualDisk(
        in VIRTUAL_STORAGE_TYPE virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        uint flags,
        nint parameters,
        out SafeFileHandle handle);

    [LibraryImport("virtdisk.dll", EntryPoint = "AttachVirtualDisk")]
    private static partial int AttachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        nint securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        in ATTACH_VIRTUAL_DISK_PARAMETERS parameters,
        nint overlapped);

    [LibraryImport("virtdisk.dll", EntryPoint = "DetachVirtualDisk")]
    private static partial int DetachVirtualDisk(SafeFileHandle virtualDiskHandle, uint flags, uint providerSpecificFlags);

    [LibraryImport("virtdisk.dll", EntryPoint = "GetVirtualDiskPhysicalPath")]
    private static partial int GetVirtualDiskPhysicalPath(SafeFileHandle virtualDiskHandle, ref uint diskPathSizeInBytes, char* diskPath);
}
