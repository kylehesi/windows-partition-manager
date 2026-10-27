using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Platform.Windows;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests.Support;

/// <summary>
/// A throwaway VHDX for one test. Every helper that touches a disk first proves the disk is a
/// virtual one, so a bug in a test can never reach a real drive.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TestVhd : IDisposable
{
    public const ulong MiB = 1024 * 1024;
    public const ulong GiB = 1024 * MiB;

    public const string GptRecovery = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
    public const string GptEfiSystem = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";

    private readonly ITestOutputHelper _output;
    private VirtualDisk? _vhd;

    private TestVhd(string path, ITestOutputHelper output)
    {
        Path = path;
        _output = output;
    }

    public string Path { get; }

    public int DiskNumber { get; private set; }

    public WmiStorageProvider Provider { get; } = new();

    public WmiStorageOperations Operations { get; } = new();

    /// <summary>Crash-proof breadcrumb trail: if the machine goes down mid-test, the last line says where.</summary>
    public static string TracePath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsPartitionManager", "test-trace.log");

    public static void Trace(string message)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TracePath)!);
        using var stream = new FileStream(TracePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
        var bytes = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}"));
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    public static TestVhd Create(ulong sizeBytes, ITestOutputHelper output, string prefix)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wpm-{prefix}-{Guid.NewGuid():N}.vhdx");
        var test = new TestVhd(path, output);
        Trace($"create {System.IO.Path.GetFileName(path)}");
        test._vhd = VirtualDisk.Create(path, sizeBytes);
        test.AttachCurrent();
        return test;
    }

    /// <summary>Surprise removal: what a crash or a pulled cable looks like to the file system.</summary>
    public void DetachAbruptly()
    {
        Trace($"surprise detach disk {DiskNumber} begin");
        _vhd?.DetachWithoutDismount();
        _vhd?.Dispose();
        _vhd = null;
        Trace($"surprise detach disk {DiskNumber} end");
    }

    public void Reattach()
    {
        Trace($"reattach {System.IO.Path.GetFileName(Path)}");
        _vhd = VirtualDisk.Open(Path);
        AttachCurrent();
    }

    public async Task<Disk> WaitAsync(Func<Disk, bool> condition, int timeoutSeconds = 60)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var disk = (await Provider.GetDisksAsync()).FirstOrDefault(d => d.Number == DiskNumber);
            if (disk is not null)
            {
                RequireVirtual(disk);
                if (condition(disk))
                {
                    return disk;
                }
            }

            if (stopwatch.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw new TimeoutException($"Disk {DiskNumber} did not reach the expected state within {timeoutSeconds} s.");
            }

            await Task.Delay(500);
        }
    }

    /// <summary>Creates a raw (unformatted) partition of a given GPT type, e.g. Recovery or EFI, the way OEM layouts have them.</summary>
    public async Task CreateRawPartitionAsync(ulong offset, ulong size, string gptType)
    {
        RequireVirtual(await WaitAsync(_ => true));

        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number = {DiskNumber}"));
        foreach (var item in searcher.Get())
        {
            using var disk = (ManagementObject)item;
            using var parameters = disk.GetMethodParameters("CreatePartition");
            parameters["Offset"] = offset;
            parameters["Size"] = size;
            parameters["GptType"] = gptType;
            using var result = disk.InvokeMethod("CreatePartition", parameters, null);
            var code = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
            if (code != 0)
            {
                throw new InvalidOperationException($"CreatePartition ({gptType}) failed with {code}.");
            }

            return;
        }

        throw new InvalidOperationException($"Disk {DiskNumber} not found.");
    }

    /// <summary>The VHDX is attached without drive letters; give a partition one so its files can be read.</summary>
    public async Task EnsureDriveLetterAsync(int partitionNumber)
    {
        RequireVirtual(await WaitAsync(_ => true));

        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {DiskNumber} AND PartitionNumber = {partitionNumber}"));
        foreach (var item in searcher.Get())
        {
            using var partition = (ManagementObject)item;
            var letter = partition["DriveLetter"];
            if (letter is char c && char.IsLetter(c))
            {
                return;
            }

            using var parameters = partition.GetMethodParameters("AddAccessPath");
            parameters["AssignDriveLetter"] = true;
            using var result = partition.InvokeMethod("AddAccessPath", parameters, null);
            _output.WriteLine($"AddAccessPath -> {result["ReturnValue"]}");
            return;
        }
    }

    /// <summary>Reads raw bytes of the virtual disk. Offsets and lengths must be sector aligned.</summary>
    public async Task<byte[]> ReadRawAsync(ulong offset, int length)
    {
        RequireVirtual(await WaitAsync(_ => true));
        using var handle = OpenPhysicalDrive(FileAccess.Read);
        var buffer = new byte[length];
        var read = RandomAccess.Read(handle, buffer, (long)offset);
        if (read != length)
        {
            throw new IOException($"Short read at {offset}: {read} of {length}.");
        }

        return buffer;
    }

    /// <summary>
    /// Writes raw bytes, only into places without a mounted file system (raw partitions, free
    /// space): the test plants markers there and later proves nothing else touched them.
    /// </summary>
    public async Task WriteRawAsync(ulong offset, byte[] data)
    {
        RequireVirtual(await WaitAsync(_ => true));
        using var handle = OpenPhysicalDrive(FileAccess.ReadWrite);
        RandomAccess.Write(handle, data, (long)offset);
        RandomAccess.FlushToDisk(handle);
    }

    /// <summary>Read-only chkdsk; exit code 0 means no problems were found.</summary>
    public int Chkdsk(char letter)
    {
        // chkdsk writes in the OEM code page (e.g. 437), which .NET only knows once registered.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var process = Process.Start(new ProcessStartInfo("chkdsk.exe", $"{letter}:")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        })!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var tail = string.Join(" | ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));
        _output.WriteLine($"chkdsk {letter}: exit {process.ExitCode} :: {tail}");
        return process.ExitCode;
    }

    /// <summary>Offset, size and type of every partition: compared before and after each operation.</summary>
    public static string LayoutOf(Disk disk) => string.Join(
        "; ",
        disk.Partitions.OrderBy(p => p.OffsetBytes).Select(p => $"#{p.Number} {p.TypeDescription} @{p.OffsetBytes} +{p.SizeBytes}"));

    public void Dispose()
    {
        Trace($"dispose: offline + detach disk {DiskNumber} and delete {System.IO.Path.GetFileName(Path)}");
        try
        {
            _vhd?.Detach();
            Trace($"dispose: disk {DiskNumber} gone");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _output.WriteLine($"Detach failed: {ex.Message}");
        }

        _vhd?.Dispose();
        try
        {
            File.Delete(Path);
        }
        catch (IOException ex)
        {
            _output.WriteLine($"Could not delete {Path}: {ex.Message}");
        }
    }

    private static void RequireVirtual(Disk disk)
    {
        var isVirtual = disk.BusType.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                        && disk.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        && !disk.IsSystem && !disk.IsBoot;
        if (!isVirtual)
        {
            throw new InvalidOperationException($"Refusing to continue: disk {disk.Number} ({disk.FriendlyName}, {disk.BusType}) is not a throwaway virtual disk.");
        }
    }

    private SafeFileHandle OpenPhysicalDrive(FileAccess access)
        => File.OpenHandle($@"\\.\PhysicalDrive{DiskNumber}", FileMode.Open, access, FileShare.ReadWrite);

    private void AttachCurrent()
    {
        _vhd!.Attach();
        DiskNumber = _vhd.GetDiskNumber();
        Trace($"attached as disk {DiskNumber}");
        _output.WriteLine($"{System.IO.Path.GetFileName(Path)} attached as disk {DiskNumber}");
    }
}
