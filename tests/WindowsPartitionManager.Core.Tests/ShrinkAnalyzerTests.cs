using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;

namespace WindowsPartitionManager.Core.Tests;

public class ShrinkAnalyzerTests
{
    private const uint ClusterSize = 4096;

    [Fact]
    public async Task PageFilePastTarget_IsReportedAsBlockerAndRaisesLimit()
    {
        // 1000 clusters; used: 0..99 (data), 500..509 (pagefile), 900..901 (a movable file).
        var volume = new FakeVolume(1000)
            .Use(0, 100)
            .AddFile(10, "pagefile.sys", FakeVolume.Root, [new ClusterExtent(500, 10)])
            .AddFile(11, "movie.mkv", FakeVolume.Root, [new ClusterExtent(900, 2)])
            .AddDirectory(12, "Videos", FakeVolume.Root);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 200 * ClusterSize);

        var blocker = Assert.Single(report.BlockersPastTarget);
        Assert.Equal(BlockerKind.PageFile, blocker.Kind);
        Assert.Equal("pagefile.sys", blocker.Path);
        Assert.Equal(510UL * ClusterSize, report.MinimumSizeWithBlockersBytes);
        Assert.Equal(1, report.MovableFilesBeyondTarget);
        Assert.Equal(2UL * ClusterSize, report.MovableBytesBeyondTarget);
        Assert.Contains(report.Advice, a => a.Contains("page file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BlockerBelowTarget_CountsButDoesNotBlock()
    {
        var volume = new FakeVolume(1000)
            .AddFile(10, "hiberfil.sys", FakeVolume.Root, [new ClusterExtent(50, 10)]);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 200 * ClusterSize);

        Assert.Empty(report.BlockersPastTarget);
        Assert.Equal(1, report.BlockersBelowTarget);
        Assert.True(report.TargetReachableNow);
        Assert.DoesNotContain(report.Advice, a => a.Contains("hibernation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ShadowCopyUnderSystemVolumeInformation_IsUnmovable()
    {
        var volume = new FakeVolume(1000)
            .AddDirectory(20, "System Volume Information", FakeVolume.Root)
            .AddFile(21, "{645da907-e9cb-49e5-8627-95a46a184277}{3808876b-c176-4e48-b7ae-04046e6cc752}", 20, [new ClusterExtent(800, 50)]);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 100 * ClusterSize);

        var blocker = Assert.Single(report.BlockersPastTarget);
        Assert.Equal(BlockerKind.ShadowCopyStorage, blocker.Kind);
        Assert.StartsWith("System Volume Information\\", blocker.Path, StringComparison.Ordinal);
        Assert.Equal(850UL * ClusterSize, report.MinimumSizeWithBlockersBytes);
        // Shadow copies can be deleted, so they do not raise the metadata-only limit.
        Assert.True(report.MinimumSizeMetadataOnlyBytes < 850UL * ClusterSize);
    }

    [Theory]
    [InlineData("tracking.log")]
    [InlineData("{invalid}{3808876b-c176-4e48-b7ae-04046e6cc752}")]
    public async Task UnrelatedSystemVolumeFileDoesNotRecommendDeletingSnapshots(string name)
    {
        var volume = new FakeVolume(1000)
            .AddDirectory(20, "System Volume Information", FakeVolume.Root)
            .AddFile(21, name, 20, [new ClusterExtent(800, 50)]);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 100 * ClusterSize);

        Assert.DoesNotContain(report.BlockersPastTarget, b => b.Kind == BlockerKind.ShadowCopyStorage);
    }

    [Fact]
    public async Task FileSystemImmovableFlag_MakesAnOrdinaryFileABlocker()
    {
        var volume = new FakeVolume(1000)
            .AddFile(30, "weird.bin", FakeVolume.Root, [new ClusterExtent(700, 4)], immovable: true);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 100 * ClusterSize);

        var blocker = Assert.Single(report.BlockersPastTarget);
        Assert.Equal(BlockerKind.FileSystemImmovable, blocker.Kind);
        Assert.Equal(704UL * ClusterSize, report.MinimumSizeMetadataOnlyBytes);
    }

    [Fact]
    public async Task MetadataLimit_ComesFromMftLocationEvenWithoutFiles()
    {
        var volume = new FakeVolume(1000, mftStartLcn: 300, mftValidBytes: 10 * ClusterSize);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 100 * ClusterSize);

        Assert.Empty(report.BlockersPastTarget);
        Assert.Equal(310UL * ClusterSize, report.MinimumSizeMetadataOnlyBytes);
        Assert.Equal(310UL * ClusterSize, report.MinimumSizeWithBlockersBytes);
    }

    [Fact]
    public async Task Limits_NeverDropBelowUsedSpace()
    {
        // 300 clusters of data spread out, no blockers anywhere near the end.
        var volume = new FakeVolume(1000).Use(0, 300);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 100 * ClusterSize);

        Assert.True(report.MinimumSizeWithBlockersBytes >= report.UsedBytes);
        Assert.True(report.MinimumSizeMetadataOnlyBytes >= report.UsedBytes);
    }

    [Fact]
    public async Task UsedClustersWithNoOwner_AreReportedAsUnattributed()
    {
        var volume = new FakeVolume(1000).Use(600, 5);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume, targetSizeBytes: 100 * ClusterSize);

        Assert.Equal(5UL * ClusterSize, report.UnattributedBytesBeyondTarget);
        Assert.False(report.TargetReachableNow);
    }

    [Fact]
    public async Task DefaultTarget_IsUsedSpacePlusHeadroom()
    {
        var volume = new FakeVolume(1000).Use(0, 100);

        var report = await ShrinkAnalyzer.AnalyzeAsync(volume);

        Assert.False(report.TargetWasExplicit);
        Assert.Equal(110UL * ClusterSize, report.TargetSizeBytes);
        Assert.Equal(100UL * ClusterSize, report.MinimumSizeWithoutMovingBytes);
    }

    [Fact]
    public async Task WindowsLimitReachingTarget_IsMentioned()
    {
        var volume = new FakeVolume(1000).Use(0, 100);

        var report = await ShrinkAnalyzer.AnalyzeAsync(
            volume,
            targetSizeBytes: 200 * ClusterSize,
            windowsSupportedSize: new SupportedSize(150 * ClusterSize, 1000 * ClusterSize));

        Assert.Contains(report.Advice, a => a.Contains("Windows itself", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hiberfil.sys", "", BlockerKind.HibernationFile)]
    [InlineData("swapfile.sys", "", BlockerKind.PageFile)]
    [InlineData("$LogFile", "", BlockerKind.NtfsMetadata)]
    [InlineData("$UsnJrnl", "$Extend", BlockerKind.NtfsMetadata)]
    public void Classify_RecognisesWellKnownUnmovableFiles(string name, string parentPath, BlockerKind expected)
    {
        var path = parentPath.Length == 0 ? name : parentPath + "\\" + name;
        var file = new FileExtents(1, name, false, [new StreamExtents(string.Empty, 0x80, false, [new ClusterExtent(1, 1)])]);

        var result = ShrinkAnalyzer.Classify(path, file);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Kind);
    }

    [Theory]
    [InlineData("Users\\me\\$money.txt")]
    [InlineData("Program Files\\app\\pagefile.sys")]
    [InlineData("Windows\\explorer.exe")]
    public void Classify_TreatsOrdinaryFilesAsMovable(string path)
    {
        var name = path[(path.LastIndexOf('\\') + 1)..];
        var file = new FileExtents(1, name, false, [new StreamExtents(string.Empty, 0x80, false, [new ClusterExtent(1, 1)])]);

        Assert.Null(ShrinkAnalyzer.Classify(path, file));
    }

    /// <summary>In-memory stand-in for a real NTFS volume.</summary>
    private sealed class FakeVolume : IVolumeInspection
    {
        public const ulong Root = 5;

        private readonly byte[] _bits;
        private readonly List<FileExtents> _files = [];
        private readonly Dictionary<ulong, (string Name, ulong Parent)> _names = new();

        public FakeVolume(ulong clusters, ulong mftStartLcn = 4, ulong mftValidBytes = ClusterSize)
        {
            _bits = new byte[(clusters + 7) / 8];
            Geometry = new VolumeGeometry
            {
                BytesPerSector = 512,
                BytesPerCluster = ClusterSize,
                TotalClusters = clusters,
                FreeClusters = clusters,
                BytesPerFileRecordSegment = 1024,
                MftStartLcn = mftStartLcn,
                MftValidDataLength = mftValidBytes,
                MftMirrorStartLcn = 2,
                MftZoneStartLcn = mftStartLcn,
                MftZoneEndLcn = mftStartLcn + 100,
            };
            Use(mftStartLcn, (mftValidBytes + ClusterSize - 1) / ClusterSize);
            Use(2, 1);
        }

        public char DriveLetter => 'X';

        public VolumeGeometry Geometry { get; }

        public ClusterBitmap Bitmap => new(_bits, Geometry.TotalClusters);

        public VolumeLayoutStats? LayoutStats => null;

        public FakeVolume Use(ulong from, ulong count)
        {
            for (var lcn = from; lcn < from + count; lcn++)
            {
                _bits[lcn >> 3] |= (byte)(1 << (int)(lcn & 7));
            }

            return this;
        }

        public FakeVolume AddFile(ulong id, string name, ulong parent, ClusterExtent[] extents, bool immovable = false)
        {
            _names[id] = (name, parent);
            _files.Add(new FileExtents(id, name, false, [new StreamExtents(string.Empty, 0x80, immovable, extents)]));
            foreach (var extent in extents)
            {
                Use(extent.StartLcn, extent.Count);
            }

            return this;
        }

        public FakeVolume AddDirectory(ulong id, string name, ulong parent)
        {
            _names[id] = (name, parent);
            _files.Add(new FileExtents(id, name, true, []));
            return this;
        }

        public Task<IReadOnlyList<FileExtents>> GetFilesAsync(ulong cutoffLcn, Func<string, bool>? includePath = null, IProgress<InspectProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<FileExtents> result = _files
                .Where(f => f.IsImmovable
                            || f.Extents.Any(e => e.EndLcn > cutoffLcn)
                            || (includePath is not null && f.Streams.Count > 0 && includePath(GetPath(f.FileId))))
                .ToList();
            return Task.FromResult(result);
        }

        public string GetPath(ulong fileId)
        {
            if (fileId == Root || !_names.TryGetValue(fileId, out var entry))
            {
                return string.Empty;
            }

            var parent = GetPath(entry.Parent);
            return parent.Length == 0 ? entry.Name : parent + "\\" + entry.Name;
        }

        public void Dispose()
        {
        }
    }
}
