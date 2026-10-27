namespace WindowsPartitionManager.Core.Analysis;

/// <summary>
/// One record of the master file table as the space analysis needs it: who its parent folder
/// is and how much disk space its streams occupy. Ids are MFT record numbers (no sequence bits).
/// </summary>
public sealed record SpaceEntry(ulong Id, ulong ParentId, string Name, bool IsDirectory, ulong AllocatedBytes);

/// <summary>A file kept for display: one of the largest in its folder or on the whole volume.</summary>
public sealed record SpaceFile(string Name, string Path, ulong SizeBytes);

/// <summary>A folder with everything below it added up.</summary>
public sealed class SpaceFolder
{
    private readonly List<SpaceFolder> _folders = [];
    private readonly List<SpaceFile> _largestFiles = [];

    internal SpaceFolder(string name, string path, SpaceFolder? parent)
    {
        Name = name;
        Path = path;
        Parent = parent;
    }

    public string Name { get; }

    /// <summary>Path relative to the volume root, empty for the root itself.</summary>
    public string Path { get; }

    public SpaceFolder? Parent { get; }

    /// <summary>Space used by this folder and everything below it, including the folder's own index.</summary>
    public ulong SizeBytes { get; internal set; }

    /// <summary>Files in this folder and below.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Sub-folders, largest first.</summary>
    public IReadOnlyList<SpaceFolder> Folders => _folders;

    /// <summary>The largest files directly in this folder, largest first (at most <see cref="SpaceAnalyzer.FilesPerFolder"/>).</summary>
    public IReadOnlyList<SpaceFile> LargestFiles => _largestFiles;

    /// <summary>Space taken by the files directly in this folder that are not in <see cref="LargestFiles"/>.</summary>
    public ulong OtherFilesBytes { get; internal set; }

    public int OtherFilesCount { get; internal set; }

    internal ulong OwnBytes { get; set; }

    internal List<SpaceFolder> FolderList => _folders;

    internal List<SpaceFile> FileList => _largestFiles;
}

public sealed record SpaceReport(char DriveLetter, SpaceFolder Root, IReadOnlyList<SpaceFile> LargestFiles, long Files, long Folders, TimeSpan Elapsed);

/// <summary>
/// Builds the folder tree from a flat MFT listing. Only folders become nodes; files are added to
/// their folder's totals, and just the largest are kept by name, so a volume with millions of files
/// still fits comfortably in memory.
/// </summary>
public static class SpaceAnalyzer
{
    /// <summary>The MFT record of the root folder.</summary>
    public const ulong RootId = 5;

    public const int FilesPerFolder = 50;

    public const int LargestFilesOverall = 100;

    public static SpaceReport Build(char driveLetter, IReadOnlyList<SpaceEntry> entries, TimeSpan elapsed = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // NTFS does not always carry the directory attribute in the record the listing reads it
        // from (seen on $Recycle.Bin and folders under AppData), so anything another record names
        // as its parent is a folder too.
        var parentIds = entries.Where(e => e.Id != e.ParentId).Select(e => e.ParentId).ToHashSet();
        var directories = new Dictionary<ulong, SpaceEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory || parentIds.Contains(entry.Id))
            {
                directories.TryAdd(entry.Id, entry);
            }
        }
        var children = new Dictionary<ulong, List<SpaceEntry>>();
        foreach (var entry in entries)
        {
            if (entry.Id == RootId)
            {
                continue;
            }

            // A parent that is missing (a torn listing) or not a folder: count it at the root.
            var parent = directories.ContainsKey(entry.ParentId) && entry.ParentId != entry.Id ? entry.ParentId : RootId;
            if (!children.TryGetValue(parent, out var list))
            {
                list = [];
                children[parent] = list;
            }

            list.Add(entry);
        }

        var root = new SpaceFolder(string.Empty, string.Empty, null)
        {
            OwnBytes = directories.TryGetValue(RootId, out var rootEntry) ? rootEntry.AllocatedBytes : 0,
        };
        var overall = new PriorityQueue<SpaceFile, ulong>();
        long files = 0;
        long folders = 0;

        // Walk the tree from the root without recursion: deep folder chains must not overflow the stack.
        var visited = new HashSet<ulong> { RootId };
        var pending = new Stack<(ulong Id, SpaceFolder Folder)>();
        pending.Push((RootId, root));
        var order = new List<SpaceFolder>();
        while (true)
        {
            if (pending.Count == 0)
            {
                // Folders the walk never reached (their parents point at each other in a damaged
                // listing) are counted under the root so their files are not lost.
                var stray = directories.Values.FirstOrDefault(d => !visited.Contains(d.Id));
                if (stray is null)
                {
                    break;
                }

                visited.Add(stray.Id);
                var lost = new SpaceFolder(stray.Name, stray.Name, root) { OwnBytes = stray.AllocatedBytes };
                root.FolderList.Add(lost);
                pending.Push((stray.Id, lost));
                folders++;
            }

            var (id, folder) = pending.Pop();
            order.Add(folder);
            if (!children.TryGetValue(id, out var list))
            {
                continue;
            }

            var local = new PriorityQueue<SpaceFile, ulong>();
            foreach (var child in list)
            {
                if (directories.ContainsKey(child.Id))
                {
                    if (!visited.Add(child.Id))
                    {
                        continue; // a loop in a damaged listing
                    }

                    var path = folder.Path.Length == 0 ? child.Name : folder.Path + "\\" + child.Name;
                    var sub = new SpaceFolder(child.Name, path, folder) { OwnBytes = child.AllocatedBytes };
                    folder.FolderList.Add(sub);
                    pending.Push((child.Id, sub));
                    folders++;
                    continue;
                }

                files++;
                folder.OwnBytes += child.AllocatedBytes;
                folder.FileCount++;
                var file = new SpaceFile(child.Name, folder.Path.Length == 0 ? child.Name : folder.Path + "\\" + child.Name, child.AllocatedBytes);
                Keep(local, file, FilesPerFolder);
                Keep(overall, file, LargestFilesOverall);
            }

            var kept = Drain(local);
            folder.FileList.AddRange(kept);
            var keptBytes = kept.Aggregate(0UL, (sum, f) => sum + f.SizeBytes);
            var directFiles = list.Where(c => !directories.ContainsKey(c.Id)).ToList();
            folder.OtherFilesCount = directFiles.Count - kept.Count;
            folder.OtherFilesBytes = directFiles.Aggregate(0UL, (sum, f) => sum + f.AllocatedBytes) - keptBytes;
        }

        // Children were visited after their parents, so adding up in reverse order works bottom-up.
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var folder = order[i];
            folder.SizeBytes += folder.OwnBytes;
            folder.FolderList.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            if (folder.Parent is { } parent)
            {
                parent.SizeBytes += folder.SizeBytes;
                parent.FileCount += folder.FileCount;
            }
        }

        return new SpaceReport(driveLetter, root, Drain(overall), files, folders, elapsed);
    }

    /// <summary>Keeps the <paramref name="limit"/> largest files in a min-heap.</summary>
    private static void Keep(PriorityQueue<SpaceFile, ulong> heap, SpaceFile file, int limit)
    {
        if (heap.Count < limit)
        {
            heap.Enqueue(file, file.SizeBytes);
        }
        else if (heap.TryPeek(out _, out var smallest) && file.SizeBytes > smallest)
        {
            heap.DequeueEnqueue(file, file.SizeBytes);
        }
    }

    private static List<SpaceFile> Drain(PriorityQueue<SpaceFile, ulong> heap)
    {
        var list = new List<SpaceFile>(heap.Count);
        while (heap.TryDequeue(out var file, out _))
        {
            list.Add(file);
        }

        list.Reverse();
        return list;
    }
}
