using System.Diagnostics;
using System.Globalization;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Platform.Windows;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows Partition Manager only runs on Windows.");
    return 1;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
LanguagePreference.Apply(LanguagePreference.LoadLanguage());
var command = args.Length == 0 ? "list" : args[0].ToLowerInvariant();

return command switch
{
    "list" => await ListAsync(),
    "analyze" => await AnalyzeAsync(args.Skip(1).ToArray()),
    "layout" => await LayoutAsync(args.Skip(1).ToArray()),
    "create" => await CreateAsync(args.Skip(1).ToArray()),
    "delete" => await DeleteAsync(args.Skip(1).ToArray()),
    "resize" => await ResizeAsync(args.Skip(1).ToArray()),
    "format" => await FormatAsync(args.Skip(1).ToArray()),
    "unblock" => await UnblockAsync(args.Skip(1).ToArray()),
    "vhd" => await VhdAsync(args.Skip(1).ToArray()),
    "-h" or "--help" or "help" => Usage(),
    _ => Usage(error: $"Unknown command '{args[0]}'."),
};

static int Usage(string? error = null)
{
    if (error is not null)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine();
    }

    Console.WriteLine("wpm list                         List disks, partitions and free regions.");
    Console.WriteLine("wpm analyze <letter> [--target <size>]");
    Console.WriteLine("                                   Explain how far an NTFS volume can shrink and which");
    Console.WriteLine("                                   files block it. Needs administrator rights.");
    Console.WriteLine("                                   <size> is the wanted volume size, e.g. 300GB or 1.5TB.");
    Console.WriteLine("wpm layout <letter> [--limit N]  Dump the raw NTFS file layout (first N files, default 15)");
    Console.WriteLine("                                   plus totals; a debugging aid. Needs administrator rights.");
    Console.WriteLine("wpm create <disk> (--size <size> | --max) [--offset <offset>] [--fs NTFS|exFAT|FAT32|ReFS]");
    Console.WriteLine("             [--label <text>] [--letter <X>] [--yes]");
    Console.WriteLine("                                   Create and format a partition in unallocated space. Without");
    Console.WriteLine("                                   --offset the largest free region is used. Asks for confirmation");
    Console.WriteLine("                                   unless --yes. Needs administrator rights.");
    Console.WriteLine("wpm delete <disk> <partition> [--yes]");
    Console.WriteLine("                                   Delete a partition and everything on it. Needs administrator rights.");
    Console.WriteLine("wpm format <disk> <partition> [--fs NTFS|exFAT|FAT32|ReFS] [--label <text>] [--full] [--yes]");
    Console.WriteLine("                                   Reformat a partition in place (keeps its letter). Everything on it is lost.");
    Console.WriteLine("wpm resize <disk> <partition> (--size <size> | --shrink-by <size> | --extend-by <size>) [--yes]");
    Console.WriteLine("                                   Shrink or extend an NTFS/ReFS partition with its file system.");
    Console.WriteLine("                                   Windows moves movable files itself. Needs administrator rights.");
    Console.WriteLine("wpm unblock <letter> [--target <size>] [--hibernation] [--pagefile] [--shadows] [--all] [--yes]");
    Console.WriteLine("                                   Remove what stops a shrink: hibernation file, page file (needs a");
    Console.WriteLine("                                   reboot), restore points. Remembers what it changed.");
    Console.WriteLine("wpm unblock --restore            Put hibernation and page file settings back the way they were.");
    Console.WriteLine("wpm vhd new <file.vhdx> --size <size>   Create a VHDX, attach it (permanently) and initialize GPT.");
    Console.WriteLine("wpm vhd detach <file.vhdx>              Detach a VHDX.");
    return error is null ? 0 : 1;
}

static async Task<int> CreateAsync(string[] args)
{
    if (args.Length == 0 || !int.TryParse(args[0], out var diskNumber))
    {
        return Usage("create needs a disk number, e.g. 'wpm create 2 --size 100GB'.");
    }

    ulong? size = null;
    ulong? offset = null;
    var useMax = false;
    var fileSystem = "NTFS";
    var label = string.Empty;
    char? letter = null;
    var yes = false;

    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--size" when i + 1 < args.Length:
                if (!ByteSize.TryParse(args[++i], out var s)) return Usage($"Could not parse size '{args[i]}'.");
                size = s;
                break;
            case "--offset" when i + 1 < args.Length:
                if (!ByteSize.TryParse(args[++i], out var o)) return Usage($"Could not parse offset '{args[i]}'.");
                offset = o;
                break;
            case "--max":
                useMax = true;
                break;
            case "--fs" when i + 1 < args.Length:
                fileSystem = args[++i];
                break;
            case "--label" when i + 1 < args.Length:
                label = args[++i];
                break;
            case "--letter" when i + 1 < args.Length:
                var text = args[++i].TrimEnd(':');
                if (text.Length != 1 || !char.IsLetter(text[0])) return Usage($"Invalid drive letter '{args[i]}'.");
                letter = char.ToUpperInvariant(text[0]);
                break;
            case "--yes" or "-y":
                yes = true;
                break;
            default:
                return Usage($"Unknown option '{args[i]}'.");
        }
    }

    if (size is null && !useMax)
    {
        return Usage("create needs --size <size> or --max.");
    }

    var provider = new WmiStorageProvider();
    var disks = await provider.GetDisksAsync();
    var disk = disks.FirstOrDefault(d => d.Number == diskNumber);
    if (disk is null)
    {
        Console.Error.WriteLine($"Disk {diskNumber} was not found.");
        return 2;
    }

    var regions = FreeSpaceCalculator.Compute(disk);
    if (offset is null)
    {
        var largest = regions.MaxBy(r => r.SizeBytes);
        if (largest is null)
        {
            Console.Error.WriteLine($"Disk {diskNumber} has no unallocated space.");
            return 5;
        }

        offset = largest.OffsetBytes;
    }

    var request = PartitionPlanner.Normalize(new CreatePartitionRequest
    {
        DiskNumber = diskNumber,
        OffsetBytes = offset.Value,
        SizeBytes = size ?? PartitionPlanner.MaximumSizeAt(disk, offset.Value),
        FileSystem = fileSystem,
        Label = label,
        DriveLetter = letter,
        ExpectedDisk = DiskFingerprint.Of(disk),
    });

    Console.WriteLine($"Plan: disk {disk.Number} ({disk.FriendlyName}, {disk.Style})");
    Console.WriteLine($"  New partition  @ {ByteSize.Format(request.OffsetBytes)}  size {ByteSize.Format(request.SizeBytes)}  {request.FileSystem}" +
                      (label.Length > 0 ? $" \"{label}\"" : string.Empty) + (letter is { } l ? $"  letter {l}:" : "  letter: automatic"));

    var issues = PartitionPlanner.Validate(disk, request, PartitionPlanner.LettersInUse(disks))
        .Concat(SafetyChecks.Power(new PowerStatusProvider().GetPowerStatus(), touchesSystemVolume: false))
        .ToList();
    foreach (var issue in issues)
    {
        Console.WriteLine($"  {(issue.IsError ? "ERROR  " : "warning")} {issue.Message}");
    }

    if (issues.Any(i => i.IsError))
    {
        Console.Error.WriteLine("Nothing was changed.");
        return 5;
    }

    if (!yes)
    {
        Console.Write("This writes to the partition table. Type YES to continue: ");
        if (Console.ReadLine()?.Trim() != "YES")
        {
            Console.WriteLine("Cancelled. Nothing was changed.");
            return 0;
        }
    }

    try
    {
        var created = await new WmiStorageOperations().CreatePartitionAsync(request, new Progress<string>(s => Console.WriteLine("  " + s)));
        var volume = created.Volume;
        Console.WriteLine($"Done: partition {created.Number} on disk {diskNumber}, {created.DriveLetter}: {volume?.FileSystem}" +
                          (volume?.Label is { Length: > 0 } vl ? $" \"{vl}\"" : string.Empty) + $", {ByteSize.Format(created.SizeBytes)}.");
        return 0;
    }
    catch (StorageOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 6;
    }
    catch (System.Management.ManagementException ex)
    {
        Console.Error.WriteLine($"Storage API error: {ex.Message}");
        Console.Error.WriteLine("If this prompt is not running as administrator, that is the likely cause.");
        return 3;
    }
}

static async Task<int> DeleteAsync(string[] args)
{
    if (args.Length < 2 || !int.TryParse(args[0], out var diskNumber) || !int.TryParse(args[1], out var partitionNumber))
    {
        return Usage("delete needs a disk number and a partition number, e.g. 'wpm delete 2 3'.");
    }

    var yes = args.Skip(2).Any(a => a is "--yes" or "-y");
    var disks = await new WmiStorageProvider().GetDisksAsync();
    var disk = disks.FirstOrDefault(d => d.Number == diskNumber);
    var partition = disk?.Partitions.FirstOrDefault(p => p.Number == partitionNumber);
    if (disk is null || partition is null)
    {
        Console.Error.WriteLine($"Partition {partitionNumber} on disk {diskNumber} was not found.");
        return 2;
    }

    var letter = partition.DriveLetter is { } l ? $"{l}: " : string.Empty;
    Console.WriteLine($"About to delete partition {partitionNumber} on disk {diskNumber} ({disk.FriendlyName}): {letter}{partition.TypeDescription}, {ByteSize.Format(partition.SizeBytes)}" +
                      (partition.Volume?.Label is { Length: > 0 } label ? $" \"{label}\"" : string.Empty));
    var deleteIssues = PartitionPlanner.ValidateDelete(disk, partition)
        .Concat(SafetyChecks.Power(new PowerStatusProvider().GetPowerStatus(), touchesSystemVolume: false))
        .ToList();
    foreach (var issue in deleteIssues)
    {
        Console.WriteLine($"  {(issue.IsError ? "ERROR  " : "warning")} {issue.Message}");
    }

    if (deleteIssues.Any(i => i.IsError))
    {
        Console.Error.WriteLine("Nothing was changed.");
        return 5;
    }

    if (!yes)
    {
        Console.Write("All data on it will be lost. Type DELETE to continue: ");
        if (Console.ReadLine()?.Trim() != "DELETE")
        {
            Console.WriteLine("Cancelled. Nothing was changed.");
            return 0;
        }
    }

    try
    {
        await new WmiStorageOperations().DeletePartitionAsync(new DeletePartitionRequest
        {
            DiskNumber = diskNumber,
            PartitionNumber = partitionNumber,
            ExpectedDisk = DiskFingerprint.Of(disk),
            ExpectedPartition = PartitionFingerprint.Of(partition),
        });
        Console.WriteLine("Deleted.");
        return 0;
    }
    catch (StorageOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 6;
    }
}

static async Task<int> FormatAsync(string[] args)
{
    if (args.Length < 2 || !int.TryParse(args[0], out var diskNumber) || !int.TryParse(args[1], out var partitionNumber))
    {
        return Usage("format needs a disk number and a partition number, e.g. 'wpm format 1 1 --fs NTFS'.");
    }

    var fileSystem = "NTFS";
    var label = string.Empty;
    var quick = true;
    var yes = false;
    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--fs" when i + 1 < args.Length: fileSystem = args[++i]; break;
            case "--label" when i + 1 < args.Length: label = args[++i]; break;
            case "--full": quick = false; break;
            case "--yes" or "-y": yes = true; break;
            default: return Usage($"Unknown option '{args[i]}'.");
        }
    }

    var disks = await new WmiStorageProvider().GetDisksAsync();
    var disk = disks.FirstOrDefault(d => d.Number == diskNumber);
    var partition = disk?.Partitions.FirstOrDefault(p => p.Number == partitionNumber);
    if (disk is null || partition is null)
    {
        Console.Error.WriteLine($"Partition {partitionNumber} on disk {diskNumber} was not found.");
        return 2;
    }

    var request = new FormatPartitionRequest
    {
        DiskNumber = diskNumber,
        PartitionNumber = partitionNumber,
        FileSystem = fileSystem,
        Label = label,
        QuickFormat = quick,
        ExpectedDisk = DiskFingerprint.Of(disk),
        ExpectedPartition = PartitionFingerprint.Of(partition),
    };
    var letter = partition.DriveLetter is { } l ? $"{l}: " : string.Empty;
    Console.WriteLine($"Plan: format partition {partitionNumber} on disk {diskNumber} ({disk.FriendlyName}): {letter}{partition.Volume?.FileSystem ?? partition.TypeDescription} -> {fileSystem}" + (label.Length > 0 ? $" \"{label}\"" : string.Empty));

    var issues = FormatPlanner.Validate(disk, partition, request)
        .Concat(SafetyChecks.Power(new PowerStatusProvider().GetPowerStatus(), touchesSystemVolume: false))
        .ToList();
    foreach (var issue in issues)
    {
        Console.WriteLine($"  {(issue.IsError ? "ERROR  " : "warning")} {issue.Message}");
    }

    if (issues.Any(i => i.IsError))
    {
        Console.Error.WriteLine("Nothing was changed.");
        return 5;
    }

    if (!yes)
    {
        Console.Write("All data on it will be lost. Type FORMAT to continue: ");
        if (Console.ReadLine()?.Trim() != "FORMAT")
        {
            Console.WriteLine("Cancelled. Nothing was changed.");
            return 0;
        }
    }

    try
    {
        var result = await new WmiStorageOperations().FormatPartitionAsync(request, new Progress<string>(s => Console.WriteLine("  " + s)));
        Console.WriteLine($"Done: {result.DriveLetter}: is now {result.Volume?.FileSystem}" + (result.Volume?.Label is { Length: > 0 } vl ? $" \"{vl}\"" : string.Empty) + ".");
        return 0;
    }
    catch (StorageOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 6;
    }
}

static async Task<int> ResizeAsync(string[] args)
{
    if (args.Length < 2 || !int.TryParse(args[0], out var diskNumber) || !int.TryParse(args[1], out var partitionNumber))
    {
        return Usage("resize needs a disk number and a partition number, e.g. 'wpm resize 0 3 --shrink-by 500GB'.");
    }

    ulong? absolute = null;
    long delta = 0;
    var yes = false;
    for (var i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--size" when i + 1 < args.Length:
                if (!ByteSize.TryParse(args[++i], out var s)) return Usage($"Could not parse size '{args[i]}'.");
                absolute = s;
                break;
            case "--shrink-by" when i + 1 < args.Length:
                if (!ByteSize.TryParse(args[++i], out var sb)) return Usage($"Could not parse size '{args[i]}'.");
                delta = -(long)sb;
                break;
            case "--extend-by" when i + 1 < args.Length:
                if (!ByteSize.TryParse(args[++i], out var eb)) return Usage($"Could not parse size '{args[i]}'.");
                delta = (long)eb;
                break;
            case "--yes" or "-y":
                yes = true;
                break;
            default:
                return Usage($"Unknown option '{args[i]}'.");
        }
    }

    if (absolute is null && delta == 0)
    {
        return Usage("resize needs --size, --shrink-by or --extend-by.");
    }

    var provider = new WmiStorageProvider();
    var disk = (await provider.GetDisksAsync()).FirstOrDefault(d => d.Number == diskNumber);
    var partition = disk?.Partitions.FirstOrDefault(p => p.Number == partitionNumber);
    if (disk is null || partition is null)
    {
        Console.Error.WriteLine($"Partition {partitionNumber} on disk {diskNumber} was not found.");
        return 2;
    }

    var newSize = absolute ?? (ulong)Math.Max(0, (long)partition.SizeBytes + delta);
    var request = ResizePlanner.Normalize(new ResizePartitionRequest
    {
        DiskNumber = diskNumber,
        PartitionNumber = partitionNumber,
        NewSizeBytes = newSize,
        ExpectedDisk = DiskFingerprint.Of(disk),
        ExpectedPartition = PartitionFingerprint.Of(partition),
    });

    Console.Error.Write("  Asking Windows for its shrink/extend limits...");
    var limits = await provider.GetSupportedSizeAsync(diskNumber, partitionNumber);
    Console.Error.Write("\r".PadRight(60) + "\r");

    var letter = partition.DriveLetter is { } l ? $"{l}: " : string.Empty;
    var direction = request.NewSizeBytes < partition.SizeBytes ? "Shrink" : "Extend";
    var change = request.NewSizeBytes < partition.SizeBytes ? partition.SizeBytes - request.NewSizeBytes : request.NewSizeBytes - partition.SizeBytes;
    Console.WriteLine($"Plan: {direction} partition {partitionNumber} on disk {diskNumber} ({letter}{partition.Volume?.FileSystem})");
    Console.WriteLine($"  {ByteSize.Format(partition.SizeBytes)}  ->  {ByteSize.Format(request.NewSizeBytes)}   ({direction.ToLowerInvariant()} by {ByteSize.Format(change)})");
    if (limits is not null)
    {
        Console.WriteLine($"  Windows allows {ByteSize.Format(limits.MinimumBytes)} .. {ByteSize.Format(limits.MaximumBytes)}");
    }

    var issues = ResizePlanner.Validate(disk, partition, request, limits)
        .Concat(SafetyChecks.Power(new PowerStatusProvider().GetPowerStatus(), touchesSystemVolume: partition.IsBoot || partition.IsSystem))
        .ToList();
    foreach (var issue in issues)
    {
        Console.WriteLine($"  {(issue.IsError ? "ERROR  " : "warning")} {issue.Message}");
    }

    if (issues.Any(i => i.IsError))
    {
        Console.Error.WriteLine("Nothing was changed.");
        return 5;
    }

    if (!yes)
    {
        Console.Write("This changes the partition table. Type YES to continue: ");
        if (Console.ReadLine()?.Trim() != "YES")
        {
            Console.WriteLine("Cancelled. Nothing was changed.");
            return 0;
        }
    }

    try
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await new WmiStorageOperations().ResizePartitionAsync(request, new Progress<string>(s => Console.WriteLine("  " + s)));
        Console.WriteLine($"Done in {stopwatch.Elapsed.TotalSeconds:F1} s: partition {result.Number} is now {ByteSize.Format(result.SizeBytes)}.");
        if (request.NewSizeBytes < partition.SizeBytes)
        {
            Console.WriteLine($"Freed {ByteSize.Format(change)} after the partition; use 'wpm create {diskNumber} --max' to turn it into a new drive.");
        }

        return 0;
    }
    catch (StorageOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 6;
    }
}

static async Task<int> UnblockAsync(string[] args)
{
    var unblock = new UnblockOperations();
    var runner = new UnblockRunner(unblock);
    var progress = new Progress<string>(s => Console.WriteLine("  " + s));

    if (args.Length > 0 && args[0] == "--restore")
    {
        if (unblock.LoadState() is not { } state)
        {
            Console.WriteLine("Nothing to restore: Windows Partition Manager has not changed any settings.");
            return 0;
        }

        Console.WriteLine($"Restoring settings changed on {LanguagePreference.FormatTimestamp(state.AppliedAt)} for {state.Volume}:" +
                          (state.HibernationWasEnabled ? " hibernation on;" : string.Empty) +
                          (state.RemovedPageFile is not null || state.PageFileWasAutomatic ? " page file setting;" : string.Empty));
        await runner.RestoreAsync(progress);
        return 0;
    }

    if (args.Length == 0 || args[0].TrimEnd(':').Length != 1 || !char.IsLetter(args[0][0]))
    {
        return Usage("unblock needs a drive letter, e.g. 'wpm unblock C: --all'.");
    }

    var letter = char.ToUpperInvariant(args[0][0]);
    ulong? target = null;
    var chosen = new HashSet<UnblockAction>();
    var all = false;
    var yes = false;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--target" or "-t" when i + 1 < args.Length:
                if (!ByteSize.TryParse(args[++i], out var t)) return Usage($"Could not parse size '{args[i]}'.");
                target = t;
                break;
            case "--hibernation": chosen.Add(UnblockAction.DisableHibernation); break;
            case "--pagefile": chosen.Add(UnblockAction.DisablePageFile); break;
            case "--shadows": chosen.Add(UnblockAction.DeleteShadowCopies); break;
            case "--all": all = true; break;
            case "--yes" or "-y": yes = true; break;
            default: return Usage($"Unknown option '{args[i]}'.");
        }
    }

    ShrinkReport report;
    try
    {
        using var volume = await new NtfsVolumeInspector().OpenAsync(letter);
        report = await ShrinkAnalyzer.AnalyzeAsync(volume, target);
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 3;
    }

    var steps = UnblockPlanner.Plan(report);
    Console.WriteLine($"Target {ByteSize.Format(report.TargetSizeBytes)}: {UnblockPlanner.Summarize(steps)}");
    if (steps.Count == 0)
    {
        if (report.BlockersPastTarget.Count > 0)
        {
            Console.WriteLine("The remaining blockers are NTFS metadata, which cannot be removed while the volume is mounted.");
        }

        return 0;
    }

    var selected = steps.Where(s => all || chosen.Contains(s.Action)).ToList();
    foreach (var step in steps)
    {
        var mark = selected.Contains(step) ? "[x]" : "[ ]";
        Console.WriteLine();
        Console.WriteLine($"{mark} {step.Title}  ({ByteSize.Format(step.BytesPastTarget)} past the target{(step.RequiresReboot ? ", needs a reboot" : string.Empty)}{(step.Reversible ? string.Empty : ", not reversible")})");
        Console.WriteLine($"    {step.Description}");
        foreach (var file in step.Files.Take(5))
        {
            Console.WriteLine($"    - {file}");
        }
    }

    if (selected.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("Nothing selected. Add --all or one of --hibernation, --pagefile, --shadows.");
        return 0;
    }

    if (!yes)
    {
        Console.WriteLine();
        Console.Write("Apply the selected steps? Type YES to continue: ");
        if (Console.ReadLine()?.Trim() != "YES")
        {
            Console.WriteLine("Cancelled. Nothing was changed.");
            return 0;
        }
    }

    try
    {
        var state = await runner.ApplyAsync(letter, selected.Select(s => s.Action), progress);
        Console.WriteLine();
        Console.WriteLine(selected.Any(s => s.RequiresReboot)
            ? "Done. Reboot, then shrink the volume. Afterwards run 'wpm unblock --restore' to put the settings back."
            : state.HasSomethingToRestore
                ? "Done. Shrink the volume now; afterwards run 'wpm unblock --restore' to put the settings back."
                : "Done. Shrink the volume now.");
        return 0;
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        Console.Error.WriteLine(ex.Message);
        if (unblock.LoadState() is { HasSomethingToRestore: true })
        {
            Console.Error.WriteLine("Settings changed before the failure are recorded; 'wpm unblock --restore' puts them back.");
        }

        return 6;
    }
}

static async Task<int> VhdAsync(string[] args)
{
    if (args.Length < 2)
    {
        return Usage("vhd needs a sub-command: new <file> --size <size>, or detach <file>.");
    }

    var path = Path.GetFullPath(args[1]);
    try
    {
        switch (args[0].ToLowerInvariant())
        {
            case "new":
            {
                var sizeIndex = Array.IndexOf(args, "--size");
                if (sizeIndex < 0 || sizeIndex + 1 >= args.Length || !ByteSize.TryParse(args[sizeIndex + 1], out var size))
                {
                    return Usage("vhd new needs --size <size>, e.g. --size 4GB.");
                }

                using var vhd = VirtualDisk.Create(path, size);
                int diskNumber;
                try
                {
                    vhd.Attach(permanent: true);
                    diskNumber = vhd.GetDiskNumber();
                }
                catch
                {
                    // Do not leave a half-made file behind when attaching fails (e.g. not elevated).
                    vhd.Dispose();
                    File.Delete(path);
                    throw;
                }
                Console.WriteLine($"Created and attached {path} as disk {diskNumber}; initializing GPT…");
                await new WmiStorageOperations().InitializeDiskAsync(diskNumber, PartitionStyle.Gpt);
                Console.WriteLine($"Disk {diskNumber} is ready. Use 'wpm list' to see it and 'wpm vhd detach \"{path}\"' when done.");
                return 0;
            }

            case "detach":
            {
                using var vhd = VirtualDisk.Open(path);
                vhd.Detach();
                Console.WriteLine($"Detached {path}.");
                return 0;
            }

            default:
                return Usage($"Unknown vhd sub-command '{args[0]}'.");
        }
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        if (ex.NativeErrorCode is 5 or 1314)
        {
            Console.Error.WriteLine("Access denied: run this from an administrator prompt.");
        }

        return 3;
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 3;
    }
    catch (StorageOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 6;
    }
}

static async Task<int> LayoutAsync(string[] args)
{
    if (args.Length == 0 || args[0].TrimEnd(':').Length != 1 || !char.IsLetter(args[0][0]))
    {
        return Usage("layout needs a drive letter, e.g. 'wpm layout D:'.");
    }

    var letter = char.ToUpperInvariant(args[0][0]);
    var limit = 15;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] is "--limit" or "-n" && i + 1 < args.Length && int.TryParse(args[++i], out var n))
        {
            limit = n;
        }
        else
        {
            return Usage($"Unknown option '{args[i]}'.");
        }
    }

    try
    {
        using var volume = await new NtfsVolumeInspector().OpenAsync(letter);
        var g = volume.Geometry;
        Console.WriteLine($"{letter}: {g.TotalClusters:N0} clusters x {g.BytesPerCluster} B, MFT @ LCN {g.MftStartLcn:N0} ({ByteSize.Format(g.MftValidDataLength)} valid), MFTMirr @ LCN {g.MftMirrorStartLcn:N0}, MFT zone {g.MftZoneStartLcn:N0}-{g.MftZoneEndLcn:N0}");

        var files = await volume.GetFilesAsync(0);
        var stats = volume.LayoutStats!;
        Console.WriteLine($"Layout pass: {stats.FileCount:N0} files, {stats.StreamCount:N0} streams, {stats.ExtentCount:N0} extents in {stats.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"Clusters: {stats.AllocatedClusters:N0} attributed to files vs {volume.Bitmap.UsedCount:N0} used in bitmap ({g.FreeClusters:N0} free per NTFS)");
        Console.WriteLine($"Immovable files: {files.Count(f => f.IsImmovable):N0}");
        Console.WriteLine();

        foreach (var file in files.OrderBy(f => f.FileId & 0x0000FFFFFFFFFFFF).Take(limit))
        {
            Console.WriteLine($"#{file.FileId & 0x0000FFFFFFFFFFFF,-8} {(file.IsDirectory ? "<DIR> " : "      ")}{volume.GetPath(file.FileId)}");
            foreach (var stream in file.Streams)
            {
                var name = stream.Name.Length == 0 ? "(data)" : stream.Name;
                var runs = string.Join(", ", stream.Extents.Take(4).Select(e => $"{e.StartLcn:N0}+{e.Count:N0}"));
                if (stream.Extents.Count > 4)
                {
                    runs += $", … ({stream.Extents.Count} runs)";
                }

                Console.WriteLine($"    {name,-24} type 0x{stream.AttributeType:X2} {(stream.IsImmovable ? "IMMOVABLE" : "         ")} {runs}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Immovable files (first 40):");
        foreach (var file in files.Where(f => f.IsImmovable).OrderBy(f => f.FurthestEndLcn).Take(40))
        {
            Console.WriteLine($"    {volume.GetPath(file.FileId),-60} ends @ {ByteSize.Format(g.ClustersToBytes(file.FurthestEndLcn))}");
        }

        return 0;
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 3;
    }
    catch (NotSupportedException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 4;
    }
}

static async Task<int> ListAsync()
{
    var stopwatch = Stopwatch.StartNew();
    IReadOnlyList<Disk> disks;
    try
    {
        disks = await new WmiStorageProvider().GetDisksAsync();
    }
    catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Could not read disk information: {ex.Message}");
        return 2;
    }

    stopwatch.Stop();

    foreach (var disk in disks)
    {
        var flags = string.Join(", ", new[]
        {
            disk.IsSystem ? "system" : null,
            disk.IsBoot ? "boot" : null,
            disk.IsReadOnly ? "read-only" : null,
            disk.IsOffline ? "offline" : null,
        }.Where(f => f is not null));

        Console.WriteLine();
        Console.WriteLine($"Disk {disk.Number}: {disk.FriendlyName}");
        Console.WriteLine($"  {ByteSize.Format(disk.SizeBytes)}  {disk.Style}  {disk.BusType}  " +
                          $"sectors {disk.LogicalSectorSize}/{disk.PhysicalSectorSize}  health {disk.Health}" +
                          (flags.Length > 0 ? $"  [{flags}]" : string.Empty));

        foreach (var segment in DiskLayout.GetSegments(disk))
        {
            var offset = ByteSize.Format(segment.OffsetBytes);
            var size = ByteSize.Format(segment.SizeBytes);

            if (segment.Partition is not { } partition)
            {
                Console.WriteLine($"  {"(free)",-6} {string.Empty,-3} {"Unallocated",-20} @ {offset,10}  {size,10}");
                continue;
            }

            var letter = partition.DriveLetter is { } l ? $"{l}:" : string.Empty;
            var line = $"  #{partition.Number,-5} {letter,-3} {partition.TypeDescription,-20} @ {offset,10}  {size,10}";

            if (partition.Volume is { } volume)
            {
                var label = string.IsNullOrEmpty(volume.Label) ? string.Empty : $" \"{volume.Label}\"";
                line += $"  {volume.FileSystem}{label}  used {ByteSize.Format(volume.UsedBytes)} / free {ByteSize.Format(volume.FreeBytes)}";
            }

            Console.WriteLine(line);
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{disks.Count} disk(s) enumerated in {stopwatch.ElapsedMilliseconds} ms.");
    return 0;
}

static async Task<int> AnalyzeAsync(string[] args)
{
    if (args.Length == 0 || args[0].TrimEnd(':').Length != 1 || !char.IsLetter(args[0][0]))
    {
        return Usage("analyze needs a drive letter, e.g. 'wpm analyze C:'.");
    }

    var letter = char.ToUpperInvariant(args[0][0]);
    ulong? target = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] is "--target" or "-t" && i + 1 < args.Length)
        {
            if (!ByteSize.TryParse(args[++i], out var parsed))
            {
                return Usage($"Could not parse size '{args[i]}'.");
            }

            target = parsed;
        }
        else
        {
            return Usage($"Unknown option '{args[i]}'.");
        }
    }

    var stopwatch = Stopwatch.StartNew();
    var storage = new WmiStorageProvider();

    // Ask Windows for its own shrink limit in parallel with our analysis; it is slow and optional.
    var supportedSizeTask = FindPartitionAsync(storage, letter)
        .ContinueWith(
            t => t.Result is { } p ? storage.GetSupportedSizeAsync(p.DiskNumber, p.PartitionNumber) : Task.FromResult<SupportedSize?>(null),
            TaskScheduler.Default)
        .Unwrap();

    var progress = new Progress<InspectProgress>(p =>
    {
        var detail = p.Total > 0 ? $"{p.Done:N0} / {p.Total:N0}" : p.Done > 0 ? p.Done.ToString("N0", CultureInfo.InvariantCulture) : string.Empty;
        Console.Error.Write($"\r  {p.Stage}... {detail}".PadRight(70));
    });

    ShrinkReport report;
    try
    {
        using var volume = await new NtfsVolumeInspector().OpenAsync(letter);
        report = await ShrinkAnalyzer.AnalyzeAsync(volume, target, null, progress);

        if (!supportedSizeTask.IsCompleted)
        {
            Console.Error.Write("\r  Waiting for Windows' own shrink estimate...".PadRight(70));
        }

        report = ShrinkAnalyzer.WithWindowsLimit(report, await WaitQuietlyAsync(supportedSizeTask));
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine("Run this command from an administrator prompt.");
        return 3;
    }
    catch (NotSupportedException ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(ex.Message);
        return 4;
    }

    Console.Error.Write("\r".PadRight(71) + "\r");
    Console.WriteLine(ShrinkReportFormatter.ToText(report));
    Console.WriteLine($"Analysis took {stopwatch.Elapsed.TotalSeconds:F1} s.");
    return 0;
}

static async Task<(int DiskNumber, int PartitionNumber)?> FindPartitionAsync(WmiStorageProvider storage, char letter)
{
    var disks = await storage.GetDisksAsync();
    foreach (var disk in disks)
    {
        foreach (var partition in disk.Partitions)
        {
            if (partition.DriveLetter == letter)
            {
                return (disk.Number, partition.Number);
            }
        }
    }

    return null;
}

static async Task<SupportedSize?> WaitQuietlyAsync(Task<SupportedSize?> task)
{
    try
    {
        return await task;
    }
    catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException or UnauthorizedAccessException)
    {
        return null;
    }
}
