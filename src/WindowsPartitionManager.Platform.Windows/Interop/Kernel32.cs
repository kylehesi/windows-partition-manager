using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsPartitionManager.Platform.Windows.Interop;

internal static unsafe partial class Kernel32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;

    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    public const uint FSCTL_QUERY_FILE_LAYOUT = 0x00090277;
    public const uint FSCTL_IS_VOLUME_DIRTY = 0x00090078;
    public const uint VOLUME_IS_DIRTY = 0x1;

    public const uint QUERY_FILE_LAYOUT_RESTART = 0x00000001;
    public const uint QUERY_FILE_LAYOUT_INCLUDE_NAMES = 0x00000002;
    public const uint QUERY_FILE_LAYOUT_INCLUDE_STREAMS = 0x00000004;
    public const uint QUERY_FILE_LAYOUT_INCLUDE_EXTENTS = 0x00000008;

    /// <summary>
    /// Without it, records whose streams own no clusters are left out entirely: small folders whose
    /// index fits in their MFT record, so everything inside them would lose its parent.
    /// </summary>
    public const uint QUERY_FILE_LAYOUT_INCLUDE_STREAMS_WITH_NO_CLUSTERS_ALLOCATED = 0x00000020; // winioctl.h

    public const uint FILE_LAYOUT_NAME_ENTRY_DOS = 0x00000002;
    public const uint STREAM_LAYOUT_ENTRY_IMMOVABLE = 0x00000001;

    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_MORE_DATA = 234;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        void* inBuffer,
        uint inBufferSize,
        void* outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    /// <summary>Output of FSCTL_GET_NTFS_VOLUME_DATA (96 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NTFS_VOLUME_DATA_BUFFER
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    /// <summary>Input of FSCTL_QUERY_FILE_LAYOUT with no filter (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QUERY_FILE_LAYOUT_INPUT
    {
        public uint NumberOfPairs;
        public uint Flags;
        public int FilterType; // 0 = QUERY_FILE_LAYOUT_FILTER_TYPE_NONE
        public uint Reserved;
        public long Filter0;   // unused union payload
        public long Filter1;
    }
}
