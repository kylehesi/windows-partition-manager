using WindowsPartitionManager.Core.Analysis;

namespace WindowsPartitionManager.Core.Tests;

public class SpaceAnalyzerTests
{
    private const ulong Root = SpaceAnalyzer.RootId;

    private static SpaceEntry Dir(ulong id, ulong parent, string name, ulong own = 4096) => new(id, parent, name, true, own);

    private static SpaceEntry File(ulong id, ulong parent, string name, ulong size) => new(id, parent, name, false, size);

    [Fact]
    public void Sizes_AddUp_FromFilesToTheRoot_AndFoldersAreSortedLargestFirst()
    {
        SpaceEntry[] entries =
        [
            Dir(Root, Root, ".", own: 0),
            Dir(10, Root, "Games", own: 0),
            Dir(11, 10, "Big", own: 0),
            Dir(12, Root, "Docs", own: 0),
            File(20, 11, "game.pak", 50_000),
            File(21, 10, "readme.txt", 1_000),
            File(22, 12, "cv.pdf", 2_000),
            File(23, Root, "pagefile.sys", 8_000),
        ];

        var report = SpaceAnalyzer.Build('D', entries);

        Assert.Equal(61_000UL, report.Root.SizeBytes);
        Assert.Equal(4, report.Files);
        Assert.Equal(3, report.Folders);
        Assert.Equal(["Games", "Docs"], report.Root.Folders.Select(f => f.Name));

        var games = report.Root.Folders[0];
        Assert.Equal(51_000UL, games.SizeBytes);
        Assert.Equal(2, games.FileCount);
        Assert.Equal(@"Games\Big", games.Folders[0].Path);
        Assert.Equal("readme.txt", Assert.Single(games.LargestFiles).Name);
        Assert.Equal(@"Games\Big\game.pak", report.LargestFiles[0].Path);
        Assert.Equal("pagefile.sys", Assert.Single(report.Root.LargestFiles).Name);
    }

    [Fact]
    public void OnlyTheLargestFilesPerFolderAreKeptByName_TheRestIsSummed()
    {
        var entries = new List<SpaceEntry> { Dir(Root, Root, ".", own: 0), Dir(10, Root, "Many", own: 0) };
        for (ulong i = 0; i < 60; i++)
        {
            entries.Add(File(100 + i, 10, $"f{i}", i + 1));
        }

        var many = SpaceAnalyzer.Build('C', entries).Root.Folders[0];

        Assert.Equal(SpaceAnalyzer.FilesPerFolder, many.LargestFiles.Count);
        Assert.Equal(60UL, many.LargestFiles[0].SizeBytes);
        Assert.Equal(10, many.OtherFilesCount);
        Assert.Equal(55UL, many.OtherFilesBytes); // 1 + 2 + ... + 10
        Assert.Equal(1830UL, many.SizeBytes);
    }

    [Fact]
    public void Orphans_AndLoops_EndUpAtTheRoot_WithoutHanging()
    {
        SpaceEntry[] entries =
        [
            Dir(Root, Root, ".", own: 0),
            File(30, 999, "orphan.bin", 500), // parent missing from the listing
            Dir(40, 41, "A", own: 0), // A and B claim each other as parent
            Dir(41, 40, "B", own: 0),
            File(42, 41, "inside-the-loop.bin", 700),
        ];

        var report = SpaceAnalyzer.Build('E', entries);

        Assert.Equal(1_200UL, report.Root.SizeBytes);
        Assert.Contains(report.Root.LargestFiles, f => f.Name == "orphan.bin");
    }
}
