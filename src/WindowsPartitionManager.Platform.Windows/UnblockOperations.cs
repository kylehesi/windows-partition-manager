using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.Json;
using WindowsPartitionManager.Core.Abstractions;
using Microsoft.Win32;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Hibernation (powercfg), page file settings (Win32_PageFileSetting / Win32_ComputerSystem) and
/// shadow copies (Win32_ShadowCopy): the system settings that make files unmovable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UnblockOperations : IUnblockOperations
{
    private const string Cimv2 = @"\\.\root\cimv2";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const string RecoveryKey = @"SOFTWARE\WindowsPartitionManager\Recovery";
    private const string RecoveryValue = "UnblockState";

    public static string StatePath => $@"HKEY_LOCAL_MACHINE\{RecoveryKey}\{RecoveryValue}";

    public bool IsHibernationEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
        return key?.GetValue("HibernateEnabled") is int value && value != 0;
    }

    public async Task SetHibernationAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var (exitCode, text) = await RunAsync(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), enabled ? "/hibernate on" : "/hibernate off", cancellationToken).ConfigureAwait(false);
        OperationLog.Append("Hibernation", enabled ? "on" : "off", exitCode == 0 ? "OK" : $"FAILED: {text}");
        if (exitCode != 0)
        {
            throw new StorageOperationException("powercfg", (uint)exitCode, text.Length > 0 ? text : "powercfg failed");
        }
    }

    public bool IsPageFileAutomatic()
    {
        var scope = new ManagementScope(Cimv2);
        scope.Connect();
        using var system = Wmi.QuerySingle(scope, "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
        return system is not null && Wmi.Get<bool>(system, "AutomaticManagedPagefile");
    }

    public IReadOnlyList<PageFileSetting> GetPageFiles()
    {
        var scope = new ManagementScope(Cimv2);
        scope.Connect();
        var result = new List<PageFileSetting>();
        foreach (var setting in Wmi.Query(scope, "SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting"))
        {
            var name = Wmi.Get<string>(setting, "Name");
            if (name is { Length: >= 2 } && char.IsLetter(name[0]))
            {
                result.Add(new PageFileSetting(char.ToUpperInvariant(name[0]), Wmi.Get<uint>(setting, "InitialSize"), Wmi.Get<uint>(setting, "MaximumSize")));
            }
        }

        return result;
    }

    public Task<PageFileChange> RemovePageFileAsync(char volume, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var scope = new ManagementScope(Cimv2);
        scope.Connect();
        var upper = char.ToUpperInvariant(volume);
        var systemDrive = char.ToUpperInvariant(Environment.SystemDirectory[0]);

        // Per-volume settings only count once automatic management is off.
        using (var system = Wmi.QuerySingle(scope, "SELECT * FROM Win32_ComputerSystem"))
        {
            if (system is not null && Wmi.Get<bool>(system, "AutomaticManagedPagefile"))
            {
                system["AutomaticManagedPagefile"] = false;
                system.Put();
            }
        }

        // "?:\pagefile.sys" is how Windows writes "system managed, on the system drive".
        PageFileSetting? removed = null;
        foreach (var setting in Wmi.Query(scope, "SELECT * FROM Win32_PageFileSetting"))
        {
            var name = Wmi.Get<string>(setting, "Name");
            if (name is not { Length: >= 1 })
            {
                continue;
            }

            var owner = name[0] == '?' ? systemDrive : char.ToUpperInvariant(name[0]);
            if (owner == upper)
            {
                removed = new PageFileSetting(upper, Wmi.Get<uint>(setting, "InitialSize"), Wmi.Get<uint>(setting, "MaximumSize"));
                setting.Delete();
            }
        }

        // Never leave the computer without a page file when another internal drive can hold one.
        char? temporary = null;
        var remaining = Wmi.Query(scope, "SELECT Name FROM Win32_PageFileSetting").Count();
        if (remaining == 0 && PickTemporaryPageFileVolume(upper, cancellationToken) is { } alternate)
        {
            using var cls = new ManagementClass(scope, new ManagementPath("Win32_PageFileSetting"), null);
            using var instance = cls.CreateInstance();
            instance["Name"] = $@"{alternate}:\pagefile.sys";
            instance["InitialSize"] = 0u;
            instance["MaximumSize"] = 0u;
            instance.Put();
            temporary = alternate;
        }

        var outcome = (removed is null ? "no setting on the volume" : "removed") +
                      (temporary is { } t ? $", temporary system-managed page file on {t}:" : remaining == 0 ? ", NO page file remains" : string.Empty);
        OperationLog.Append("PageFile", $"remove volume={upper}", $"OK: {outcome} (effective after reboot)");
        return new PageFileChange(removed, temporary);
    }, cancellationToken);

    public Task RestorePageFileAsync(PageFileSetting? removed, bool automatic, char? temporaryVolume, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var scope = new ManagementScope(Cimv2);
        scope.Connect();

        var current = GetPageFiles();
        if (IsPageFileAutomatic() || (removed is not null && current.Any(p => p.Volume == removed.Volume)))
        {
            throw new InvalidOperationException("Paging configuration changed since the unblock operation. Review it before restoring.");
        }

        if (temporaryVolume is { } temp)
        {
            foreach (var setting in Wmi.Query(scope, "SELECT * FROM Win32_PageFileSetting"))
            {
                if (string.Equals(Wmi.Get<string>(setting, "Name"), $@"{temp}:\pagefile.sys", StringComparison.OrdinalIgnoreCase))
                {
                    if (Wmi.Get<uint>(setting, "InitialSize") != 0 || Wmi.Get<uint>(setting, "MaximumSize") != 0)
                    {
                        throw new InvalidOperationException("The temporary paging file has been reconfigured.");
                    }

                    setting.Delete();
                }
            }
        }

        if (automatic)
        {
            using var system = Wmi.QuerySingle(scope, "SELECT * FROM Win32_ComputerSystem");
            if (system is not null)
            {
                system["AutomaticManagedPagefile"] = true;
                system.Put();
            }
        }
        else if (removed is not null)
        {
            using var cls = new ManagementClass(scope, new ManagementPath("Win32_PageFileSetting"), null);
            using var instance = cls.CreateInstance();
            instance["Name"] = $@"{char.ToUpperInvariant(removed.Volume)}:\pagefile.sys";
            instance["InitialSize"] = removed.InitialMb;
            instance["MaximumSize"] = removed.MaximumMb;
            instance.Put(new PutOptions { Type = PutType.CreateOnly });
        }

        var what = automatic ? "automatic management" : removed is not null ? $"volume={removed.Volume} {removed.InitialMb}-{removed.MaximumMb} MB" : "nothing to put back";
        OperationLog.Append("PageFile", $"restore {what}" + (temporaryVolume is { } tv ? $", temporary on {tv}: removed" : string.Empty), "OK (effective after reboot)");
    }, cancellationToken);

    /// <summary>
    /// An internal (not USB, not virtual) NTFS volume with at least 8 GB free, other than the one
    /// being shrunk; the one with the most free space wins.
    /// </summary>
    private static char? PickTemporaryPageFileVolume(char excluded, CancellationToken cancellationToken)
    {
        const ulong MinimumFree = 8UL * 1024 * 1024 * 1024;
        string[] unsuitableBuses = ["USB", "SD", "MMC", "Virtual", "File-backed virtual", "Unknown"];

        return WmiStorageProvider.GetDisks(cancellationToken)
            .Where(d => !d.IsOffline && !d.IsReadOnly && !unsuitableBuses.Contains(d.BusType, StringComparer.OrdinalIgnoreCase))
            .SelectMany(d => d.Partitions)
            .Where(p => p.DriveLetter is { } l && l != excluded
                        && p.Volume is { } v
                        && v.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                        && v.FreeBytes >= MinimumFree)
            .OrderByDescending(p => p.Volume!.FreeBytes)
            .Select(p => p.DriveLetter)
            .FirstOrDefault();
    }

    public int CountShadowCopies(char volume)
    {
        var scope = new ManagementScope(Cimv2);
        scope.Connect();
        var target = VolumePathOf(volume);
        return target is null ? 0 : Wmi.Query(scope, "SELECT VolumeName FROM Win32_ShadowCopy").Count(s => string.Equals(Wmi.Get<string>(s, "VolumeName"), target, StringComparison.OrdinalIgnoreCase));
    }

    public Task<int> DeleteShadowCopiesAsync(char volume, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var scope = new ManagementScope(Cimv2);
        scope.Connect();
        var target = VolumePathOf(volume);
        if (target is null)
        {
            return 0;
        }

        var deleted = 0;
        // Delete inside the enumeration: each object is only valid until the iterator moves on.
        foreach (var shadow in Wmi.Query(scope, "SELECT * FROM Win32_ShadowCopy"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Wmi.Get<string>(shadow, "VolumeName"), target, StringComparison.OrdinalIgnoreCase))
            {
                shadow.Delete();
                deleted++;
            }
        }

        OperationLog.Append("ShadowCopies", $"delete volume={char.ToUpperInvariant(volume)}", $"OK ({deleted} deleted)");
        return deleted;
    }, cancellationToken);

    public UnblockState? LoadState()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RecoveryKey);
        return key?.GetValue(RecoveryValue) is string json
            ? JsonSerializer.Deserialize<UnblockState>(json, JsonOptions)
            : null;
    }

    public void SaveState(UnblockState? state)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RecoveryKey);
        if (state is null)
        {
            key.DeleteValue(RecoveryValue, throwOnMissingValue: false);
            return;
        }

        key.SetValue(RecoveryValue, JsonSerializer.Serialize(state, JsonOptions), RegistryValueKind.String);
    }

    /// <summary>Win32_ShadowCopy.VolumeName is the volume GUID path; map a drive letter to it.</summary>
    private static string? VolumePathOf(char volume)
    {
        var scope = Wmi.Connect();
        foreach (var v in Wmi.Query(scope, "SELECT Path, DriveLetter FROM MSFT_Volume"))
        {
            if (Wmi.ReadDriveLetter(v) == char.ToUpperInvariant(volume))
            {
                return Wmi.Get<string>(v, "Path");
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not start {file}.");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, (output + error).Trim());
    }
}
