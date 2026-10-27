using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Platform.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Read-only NTFS inspection built on documented control codes: the volume allocation bitmap and
/// FSCTL_QUERY_FILE_LAYOUT, which returns names, streams and cluster extents for every MFT record
/// (metadata, page file and shadow copies included) without opening a single file. Needs
/// administrator rights because Windows only grants raw volume handles to elevated processes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NtfsVolumeInspector : IVolumeInspector
{
    public Task<IVolumeInspection> OpenAsync(char driveLetter, CancellationToken cancellationToken = default)
        => Task.Run(() => (IVolumeInspection)NtfsVolumeInspection.Open(driveLetter), cancellationToken);
}

[SupportedOSPlatform("windows")]
internal sealed unsafe class NtfsVolumeInspection : IVolumeInspection
{
    private const ulong RootFileId = 5;
    private const ulong FileIdMask = 0x0000FFFFFFFFFFFF; // low 48 bits: MFT record number without the sequence number
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint ATTRIBUTE_INDEX_ALLOCATION = 0xA0;

    private readonly SafeFileHandle _volume;
    private readonly Lock _layoutLock = new();

    // Compact layout tables filled by one pass over FSCTL_QUERY_FILE_LAYOUT.
    private readonly List<FileRecord> _files = new(1 << 16);
    private readonly List<StreamRecord> _streams = new(1 << 16);
    private readonly List<ClusterExtent> _extents = new(1 << 17);
    private readonly Dictionary<ulong, int> _fileIndexById = new(1 << 16);
    private readonly Dictionary<ulong, string> _pathCache = new();
    private bool _layoutLoaded;

    private NtfsVolumeInspection(char driveLetter, SafeFileHandle volume, VolumeGeometry geometry, ClusterBitmap bitmap)
    {
        DriveLetter = driveLetter;
        _volume = volume;
        Geometry = geometry;
        Bitmap = bitmap;
    }

    private readonly record struct FileRecord(ulong FileId, string Name, ulong ParentId, bool IsDirectory, int FirstStream, int StreamCount, bool Immovable);

    private readonly record struct StreamRecord(string Name, uint AttributeType, bool Immovable, int FirstExtent, int ExtentCount, ulong FurthestEndLcn);

    public char DriveLetter { get; }

    public VolumeGeometry Geometry { get; }

    public ClusterBitmap Bitmap { get; }

    public VolumeLayoutStats? LayoutStats { get; private set; }

    public static NtfsVolumeInspection Open(char driveLetter)
    {
        var volume = Kernel32.CreateFile(
            $@"\\.\{char.ToUpperInvariant(driveLetter)}:",
            Kernel32.GENERIC_READ,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            nint.Zero,
            Kernel32.OPEN_EXISTING,
            0,
            nint.Zero);

        if (volume.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            volume.Dispose();
            throw error == Kernel32.ERROR_ACCESS_DENIED
                ? new UnauthorizedAccessException(CoreText.Format("Ops_AdminRequired", driveLetter))
                : new Win32Exception(error);
        }

        try
        {
            var geometry = ReadGeometry(volume);
            var bitmap = ReadBitmap(volume, geometry.TotalClusters);
            return new NtfsVolumeInspection(driveLetter, volume, geometry, bitmap);
        }
        catch
        {
            volume.Dispose();
            throw;
        }
    }

    public Task<IReadOnlyList<FileExtents>> GetFilesAsync(
        ulong cutoffLcn,
        Func<string, bool>? includePath = null,
        IProgress<InspectProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<FileExtents>>(() => GetFiles(cutoffLcn, includePath, progress, cancellationToken), cancellationToken);

    public string GetPath(ulong fileId)
    {
        lock (_layoutLock)
        {
            return BuildPath(fileId & FileIdMask, 0);
        }
    }

    public void Dispose() => _volume.Dispose();

    private List<FileExtents> GetFiles(ulong cutoffLcn, Func<string, bool>? includePath, IProgress<InspectProgress>? progress, CancellationToken cancellationToken)
    {
        lock (_layoutLock)
        {
            EnsureLayout(progress, cancellationToken);

            progress?.Report(new InspectProgress(CoreText.Get("Analysis_SelectingFiles"), 0, _files.Count));
            var results = new List<FileExtents>();

            for (var i = 0; i < _files.Count; i++)
            {
                if ((i & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new InspectProgress(CoreText.Get("Analysis_SelectingFiles"), i, _files.Count));
                }

                var file = _files[i];
                var keep = file.Immovable;

                if (!keep)
                {
                    for (var s = file.FirstStream; s < file.FirstStream + file.StreamCount; s++)
                    {
                        if (_streams[s].FurthestEndLcn > cutoffLcn)
                        {
                            keep = true;
                            break;
                        }
                    }
                }

                if (!keep && includePath is not null && file.StreamCount > 0)
                {
                    keep = includePath(BuildPath(file.FileId & FileIdMask, 0));
                }

                if (keep)
                {
                    results.Add(ToFileExtents(file));
                }
            }

            progress?.Report(new InspectProgress(CoreText.Get("Analysis_SelectingFiles"), _files.Count, _files.Count));
            return results;
        }
    }

    private FileExtents ToFileExtents(FileRecord file)
    {
        var streams = new StreamExtents[file.StreamCount];
        for (var s = 0; s < file.StreamCount; s++)
        {
            var stream = _streams[file.FirstStream + s];
            var extents = new ClusterExtent[stream.ExtentCount];
            _extents.CopyTo(stream.FirstExtent, extents, 0, stream.ExtentCount);
            streams[s] = new StreamExtents(stream.Name, stream.AttributeType, stream.Immovable, extents);
        }

        return new FileExtents(file.FileId, file.Name, file.IsDirectory, streams);
    }

    private string BuildPath(ulong key, int depth)
    {
        if (key == RootFileId || depth > 512)
        {
            return string.Empty;
        }

        if (_pathCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!_fileIndexById.TryGetValue(key, out var index))
        {
            return $"<{key}>";
        }

        var entry = _files[index];
        var parentPath = BuildPath(entry.ParentId & FileIdMask, depth + 1);
        var path = parentPath.Length == 0 ? entry.Name : parentPath + "\\" + entry.Name;

        if (entry.IsDirectory)
        {
            _pathCache[key] = path;
        }

        return path;
    }

    private void EnsureLayout(IProgress<InspectProgress>? progress, CancellationToken cancellationToken)
    {
        if (_layoutLoaded)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new InspectProgress(CoreText.Get("Analysis_ReadingLayout"), 0, 0));

        var buffer = new byte[4 * 1024 * 1024];
        var input = new Kernel32.QUERY_FILE_LAYOUT_INPUT
        {
            Flags = Kernel32.QUERY_FILE_LAYOUT_RESTART
                  | Kernel32.QUERY_FILE_LAYOUT_INCLUDE_NAMES
                  | Kernel32.QUERY_FILE_LAYOUT_INCLUDE_STREAMS
                  | Kernel32.QUERY_FILE_LAYOUT_INCLUDE_EXTENTS
                  | Kernel32.QUERY_FILE_LAYOUT_INCLUDE_STREAMS_WITH_NO_CLUSTERS_ALLOCATED, // keep small folders, or paths below them break
        };
        ulong allocated = 0;

        fixed (byte* output = buffer)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ok = Kernel32.DeviceIoControl(
                    _volume,
                    Kernel32.FSCTL_QUERY_FILE_LAYOUT,
                    &input,
                    (uint)sizeof(Kernel32.QUERY_FILE_LAYOUT_INPUT),
                    output,
                    (uint)buffer.Length,
                    out var bytesReturned,
                    nint.Zero);

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
                    allocated += ParseFileEntry(span, offset);
                    var next = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]);
                    if (next == 0)
                    {
                        break;
                    }

                    offset += next;
                }

                progress?.Report(new InspectProgress(CoreText.Get("Analysis_ReadingLayout"), _files.Count, 0));
            }
        }

        _layoutLoaded = true;
        LayoutStats = new VolumeLayoutStats(_files.Count, _streams.Count, _extents.Count, allocated, stopwatch.Elapsed);
    }

    /// <summary>Parses one FILE_LAYOUT_ENTRY with its names and streams; returns the clusters it allocates.</summary>
    private ulong ParseFileEntry(ReadOnlySpan<byte> span, int at)
    {
        // FILE_LAYOUT_ENTRY: uint Version, uint NextFileOffset, uint Flags, uint FileAttributes,
        //                    ulong FileReferenceNumber, uint FirstNameOffset, uint FirstStreamOffset,
        //                    uint ExtraInfoOffset, uint ExtraInfoLength.
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 12)..]);
        var fileId = BinaryPrimitives.ReadUInt64LittleEndian(span[(at + 16)..]);
        var nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 24)..]);
        var streamOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 28)..]);

        var (name, parentId) = ReadName(span, at, nameOffset);

        var firstStream = _streams.Count;
        var immovable = false;
        ulong allocated = 0;

        var cursor = streamOffset == 0 ? 0 : at + streamOffset;
        while (cursor > 0 && cursor + 48 <= span.Length)
        {
            // STREAM_LAYOUT_ENTRY: uint Version, uint NextStreamOffset, uint Flags, uint ExtentInformationOffset,
            //                      LARGE_INTEGER AllocationSize, LARGE_INTEGER EndOfFile, uint StreamInformationOffset,
            //                      uint AttributeTypeCode, uint AttributeFlags, uint StreamIdentifierLength, WCHAR StreamIdentifier[].
            var nextStream = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 4)..]);
            var streamFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 8)..]);
            var extentOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 12)..]);
            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 36)..]);
            var idLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 44)..]);

            // The identifier looks like "::$DATA", ":Zone.Identifier:$DATA" or ":$I30:$INDEX_ALLOCATION";
            // keep only the stream name in the middle.
            var streamName = string.Empty;
            if (idLength > 0 && cursor + 48 + idLength <= span.Length)
            {
                var identifier = Encoding.Unicode.GetString(span.Slice(cursor + 48, idLength)).TrimEnd('\0');
                var parts = identifier.Split(':');
                streamName = parts.Length >= 3 ? parts[1] : identifier;
            }

            if (attributeType == ATTRIBUTE_INDEX_ALLOCATION && streamName.Length == 0)
            {
                streamName = "$I30";
            }

            var streamImmovable = (streamFlags & Kernel32.STREAM_LAYOUT_ENTRY_IMMOVABLE) != 0;
            var firstExtent = _extents.Count;
            ulong furthest = 0;

            if (extentOffset != 0)
            {
                // STREAM_EXTENT_ENTRY: uint Flags, pad, then RETRIEVAL_POINTERS_BUFFER
                // (uint ExtentCount, pad, long StartingVcn, {long NextVcn, long Lcn}[]).
                var e = cursor + extentOffset;
                if (e + 24 <= span.Length)
                {
                    var extentCount = BinaryPrimitives.ReadUInt32LittleEndian(span[(e + 8)..]);
                    var previousVcn = BinaryPrimitives.ReadInt64LittleEndian(span[(e + 16)..]);
                    var pair = e + 24;

                    for (var i = 0; i < extentCount && pair + 16 <= span.Length; i++, pair += 16)
                    {
                        var nextVcn = BinaryPrimitives.ReadInt64LittleEndian(span[pair..]);
                        var lcn = BinaryPrimitives.ReadInt64LittleEndian(span[(pair + 8)..]);

                        if (lcn >= 0 && nextVcn > previousVcn)
                        {
                            var extent = new ClusterExtent((ulong)lcn, (ulong)(nextVcn - previousVcn));
                            _extents.Add(extent);
                            allocated += extent.Count;
                            furthest = Math.Max(furthest, extent.EndLcn);
                        }

                        previousVcn = nextVcn;
                    }
                }
            }

            var extentsAdded = _extents.Count - firstExtent;
            if (extentsAdded > 0)
            {
                _streams.Add(new StreamRecord(streamName, attributeType, streamImmovable, firstExtent, extentsAdded, furthest));
                immovable |= streamImmovable;
            }

            cursor = nextStream == 0 ? 0 : cursor + nextStream;
        }

        var record = new FileRecord(
            fileId,
            name,
            parentId,
            (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
            firstStream,
            _streams.Count - firstStream,
            immovable);

        _fileIndexById[fileId & FileIdMask] = _files.Count;
        _files.Add(record);
        return allocated;
    }

    internal static (string Name, ulong ParentId) ReadName(ReadOnlySpan<byte> span, int fileAt, int nameOffset)
    {
        string? fallback = null;
        ulong fallbackParent = RootFileId;

        var cursor = nameOffset == 0 ? 0 : fileAt + nameOffset;
        while (cursor > 0 && cursor + 24 <= span.Length)
        {
            // FILE_LAYOUT_NAME_ENTRY: uint NextNameOffset, uint Flags, ulong ParentFileReferenceNumber,
            //                         uint FileNameLength (bytes), uint Reserved, WCHAR FileName[].
            var nextName = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[cursor..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 4)..]);
            var parent = BinaryPrimitives.ReadUInt64LittleEndian(span[(cursor + 8)..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(cursor + 16)..]);

            if (length > 0 && cursor + 24 + length <= span.Length)
            {
                var text = Encoding.Unicode.GetString(span.Slice(cursor + 24, length));
                var isDosOnly = (flags & Kernel32.FILE_LAYOUT_NAME_ENTRY_DOS) != 0 && (flags & 0x1) == 0;
                if (!isDosOnly)
                {
                    return (text, parent);
                }

                fallback ??= text;
                fallbackParent = parent;
            }

            cursor = nextName == 0 ? 0 : cursor + nextName;
        }

        return (fallback ?? string.Empty, fallbackParent);
    }

    private static VolumeGeometry ReadGeometry(SafeFileHandle volume)
    {
        Kernel32.NTFS_VOLUME_DATA_BUFFER data;
        var ok = Kernel32.DeviceIoControl(
            volume,
            Kernel32.FSCTL_GET_NTFS_VOLUME_DATA,
            null,
            0,
            &data,
            (uint)sizeof(Kernel32.NTFS_VOLUME_DATA_BUFFER),
            out _,
            nint.Zero);

        if (!ok)
        {
            var error = Marshal.GetLastPInvokeError();
            throw error == Kernel32.ERROR_INVALID_FUNCTION
                ? new NotSupportedException(CoreText.Get("Ops_NotNtfs"))
                : new Win32Exception(error, "FSCTL_GET_NTFS_VOLUME_DATA failed");
        }

        return new VolumeGeometry
        {
            BytesPerSector = data.BytesPerSector,
            BytesPerCluster = data.BytesPerCluster,
            TotalClusters = (ulong)data.TotalClusters,
            FreeClusters = (ulong)data.FreeClusters,
            BytesPerFileRecordSegment = data.BytesPerFileRecordSegment,
            MftStartLcn = (ulong)data.MftStartLcn,
            MftValidDataLength = (ulong)data.MftValidDataLength,
            MftMirrorStartLcn = (ulong)data.Mft2StartLcn,
            MftZoneStartLcn = (ulong)data.MftZoneStart,
            MftZoneEndLcn = (ulong)data.MftZoneEnd,
        };
    }

    private static ClusterBitmap ReadBitmap(SafeFileHandle volume, ulong totalClusters)
    {
        const int HeaderSize = 16; // long StartingLcn, long BitmapSize
        var bits = new byte[(totalClusters + 7) / 8];
        var buffer = new byte[4 * 1024 * 1024 + HeaderSize];
        ulong startLcn = 0;

        fixed (byte* output = buffer)
        {
            while (startLcn < totalClusters)
            {
                var input = (long)startLcn;
                var ok = Kernel32.DeviceIoControl(
                    volume,
                    Kernel32.FSCTL_GET_VOLUME_BITMAP,
                    &input,
                    sizeof(long),
                    output,
                    (uint)buffer.Length,
                    out var bytesReturned,
                    nint.Zero);

                var error = ok ? 0 : Marshal.GetLastPInvokeError();
                if (!ok && error != Kernel32.ERROR_MORE_DATA)
                {
                    throw new Win32Exception(error, "FSCTL_GET_VOLUME_BITMAP failed");
                }

                if (bytesReturned <= HeaderSize)
                {
                    break;
                }

                var returnedStart = (ulong)BinaryPrimitives.ReadInt64LittleEndian(buffer);
                var payload = (int)bytesReturned - HeaderSize;
                var destination = (int)(returnedStart / 8);
                var toCopy = Math.Min(payload, bits.Length - destination);
                if (toCopy <= 0)
                {
                    break;
                }

                Buffer.BlockCopy(buffer, HeaderSize, bits, destination, toCopy);
                startLcn = returnedStart + (ulong)toCopy * 8;

                if (ok)
                {
                    break;
                }
            }
        }

        return new ClusterBitmap(bits, totalClusters);
    }
}
