using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Platform.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// One pass over FSCTL_QUERY_FILE_LAYOUT with names and streams but without extents: enough to
/// know every file's folder and the space its streams take, which is all a space analysis needs.
/// This is what makes it fast; nothing walks the directory tree through the file system.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class NtfsSpaceReader : IVolumeSpaceReader
{
    private const ulong FileIdMask = 0x0000FFFFFFFFFFFF;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    /// <summary>Resident data lives inside its MFT record, which the $MFT file already counts.</summary>
    private const uint STREAM_LAYOUT_ENTRY_RESIDENT = 0x4;

    public Task<IReadOnlyList<SpaceEntry>> ReadAsync(char driveLetter, IProgress<InspectProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<SpaceEntry>>(() => Read(driveLetter, progress, cancellationToken), cancellationToken);

    private static List<SpaceEntry> Read(char driveLetter, IProgress<InspectProgress>? progress, CancellationToken cancellationToken)
    {
        using var volume = Open(driveLetter);
        var entries = new List<SpaceEntry>(1 << 16);
        var buffer = new byte[4 * 1024 * 1024];
        var input = new Kernel32.QUERY_FILE_LAYOUT_INPUT
        {
            Flags = Kernel32.QUERY_FILE_LAYOUT_RESTART | Kernel32.QUERY_FILE_LAYOUT_INCLUDE_NAMES | Kernel32.QUERY_FILE_LAYOUT_INCLUDE_STREAMS
                  | Kernel32.QUERY_FILE_LAYOUT_INCLUDE_STREAMS_WITH_NO_CLUSTERS_ALLOCATED,
        };

        progress?.Report(new InspectProgress(CoreText.Get("Analysis_ReadingLayout"), 0, 0));
        fixed (byte* output = buffer)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ok = Kernel32.DeviceIoControl(volume, Kernel32.FSCTL_QUERY_FILE_LAYOUT, &input, (uint)sizeof(Kernel32.QUERY_FILE_LAYOUT_INPUT), output, (uint)buffer.Length, out var bytesReturned, nint.Zero);
                if (!ok)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == Kernel32.ERROR_HANDLE_EOF)
                    {
                        break;
                    }

                    throw error is Kernel32.ERROR_INVALID_FUNCTION or Kernel32.ERROR_NOT_SUPPORTED or Kernel32.ERROR_INVALID_PARAMETER
                        ? new NotSupportedException(CoreText.Get("Ops_NoFileLayout"))
                        : new Win32Exception(error, "FSCTL_QUERY_FILE_LAYOUT failed");
                }

                input.Flags &= ~Kernel32.QUERY_FILE_LAYOUT_RESTART;
                var span = buffer.AsSpan(0, (int)bytesReturned);
                if (span.Length < 16)
                {
                    break;
                }

                // QUERY_FILE_LAYOUT_OUTPUT: uint FileEntryCount, uint FirstFileOffset, uint Flags, uint Reserved.
                var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(span);
                var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                if (fileCount == 0)
                {
                    break;
                }

                for (var i = 0; i < fileCount && offset > 0 && offset + 40 <= span.Length; i++)
                {
                    entries.Add(ParseEntry(span, offset));
                    var next = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]);
                    if (next == 0)
                    {
                        break;
                    }

                    offset += next;
                }

                progress?.Report(new InspectProgress(CoreText.Get("Analysis_ReadingLayout"), entries.Count, 0));
            }
        }

        return entries;
    }

    private static SafeFileHandle Open(char driveLetter)
    {
        var volume = Kernel32.CreateFile($@"\\.\{char.ToUpperInvariant(driveLetter)}:", Kernel32.GENERIC_READ, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, nint.Zero, Kernel32.OPEN_EXISTING, 0, nint.Zero);
        if (!volume.IsInvalid)
        {
            return volume;
        }

        var error = Marshal.GetLastPInvokeError();
        volume.Dispose();
        throw error == Kernel32.ERROR_ACCESS_DENIED
            ? new UnauthorizedAccessException(CoreText.Format("Ops_AdminRequired", driveLetter))
            : new Win32Exception(error);
    }

    /// <summary>FILE_LAYOUT_ENTRY with its first name and the allocation size of every non-resident stream.</summary>
    private static SpaceEntry ParseEntry(ReadOnlySpan<byte> span, int at)
    {
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 12)..]);
        var fileId = BinaryPrimitives.ReadUInt64LittleEndian(span[(at + 16)..]);
        var nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 24)..]);
        var streamOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 28)..]);
        var (name, parentId) = NtfsVolumeInspection.ReadName(span, at, nameOffset);

        ulong allocated = 0;
        var cursor = streamOffset == 0 ? 0 : at + streamOffset;
        while (cursor > 0 && cursor + 48 <= span.Length)
        {
            // STREAM_LAYOUT_ENTRY: uint Version, uint NextStreamOffset, uint Flags, uint ExtentInformationOffset,
            //                      LARGE_INTEGER AllocationSize, ...
            var nextStream = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 4)..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 8)..]);
            var allocation = BinaryPrimitives.ReadInt64LittleEndian(span[(cursor + 16)..]);
            if ((flags & STREAM_LAYOUT_ENTRY_RESIDENT) == 0 && allocation > 0)
            {
                allocated += (ulong)allocation;
            }

            cursor = nextStream == 0 ? 0 : cursor + nextStream;
        }

        return new SpaceEntry(fileId & FileIdMask, parentId & FileIdMask, name, (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0, allocated);
    }
}
