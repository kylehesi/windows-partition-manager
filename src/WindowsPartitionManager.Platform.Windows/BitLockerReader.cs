using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Reads BitLocker's view of each volume from Win32_EncryptableVolume (the class behind
/// manage-bde and Get-BitLockerVolume). It needs administrator rights; without them, or on a
/// Windows edition without the class, every volume simply has no BitLocker information.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BitLockerReader
{
    private const string Namespace = @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption";

    /// <summary>Status per volume GUID path (<c>\\?\Volume{...}\</c>); empty when BitLocker cannot be queried.</summary>
    public static Dictionary<string, BitLockerStatus> Read()
    {
        var result = new Dictionary<string, BitLockerStatus>(StringComparer.OrdinalIgnoreCase);

        // Without elevation the query is refused, but only after about five seconds; do not wait for that.
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                return result;
            }
        }

        try
        {
            var scope = new ManagementScope(Namespace);
            scope.Connect();
            foreach (var volume in Wmi.Query(scope, "SELECT * FROM Win32_EncryptableVolume"))
            {
                if (Wmi.Get<string>(volume, "DeviceID") is not { Length: > 0 } path)
                {
                    continue;
                }

                result[path] = ReadOne(volume);
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Not elevated, or no BitLocker on this edition: the volumes keep BitLocker = null.
        }

        return result;
    }

    private static BitLockerStatus ReadOne(ManagementObject volume)
    {
        var state = EncryptionState.Unknown;
        int? percent = null;
        using (var conversion = Call(volume, "GetConversionStatus"))
        {
            if (conversion is not null)
            {
                state = Number(conversion, "ConversionStatus") switch
                {
                    0 => EncryptionState.NotEncrypted,
                    1 => EncryptionState.Encrypted,
                    2 => EncryptionState.Encrypting,
                    3 => EncryptionState.Decrypting,
                    4 or 5 => EncryptionState.Paused,
                    _ => EncryptionState.Unknown,
                };
                percent = (int?)Number(conversion, "EncryptionPercentage");
            }
        }

        bool locked;
        using (var lockStatus = Call(volume, "GetLockStatus"))
        {
            locked = lockStatus is not null && Number(lockStatus, "LockStatus") == 1;
        }

        // ProtectionStatus: 0 off (also "suspended"), 1 on, 2 unknown (e.g. while locked).
        var protection = Number(volume, "ProtectionStatus") == 1 || locked;

        // A locked volume reports its conversion status as unknown; it is encrypted all the same.
        if (locked && state == EncryptionState.Unknown)
        {
            state = EncryptionState.Encrypted;
        }

        return new BitLockerStatus(state, protection, locked, percent);
    }

    /// <summary>Invokes a parameterless method; null when it is not supported or fails.</summary>
    private static ManagementBaseObject? Call(ManagementObject target, string method)
    {
        try
        {
            var result = target.InvokeMethod(method, null, null);
            return result is not null && Number(result, "ReturnValue") == 0 ? result : null;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static ulong? Number(ManagementBaseObject mo, string property)
        => Wmi.GetRaw(mo, property) is { } value ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : null;
}
