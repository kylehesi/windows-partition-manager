namespace WindowsPartitionManager.Core.Model;

/// <summary>What Windows reports about a volume's file system integrity (MSFT_Volume.HealthStatus).</summary>
public enum VolumeHealth
{
    Unknown,
    Healthy,
    ScanNeeded,
    SpotFixNeeded,
    FullRepairNeeded,
}

/// <summary>Where BitLocker stands on a volume (Win32_EncryptableVolume conversion status).</summary>
public enum EncryptionState
{
    NotEncrypted,
    Encrypted,
    Encrypting,
    Decrypting,
    Paused,
    Unknown,
}

/// <summary>
/// BitLocker as Windows reports it. <see cref="ProtectionOn"/> is false for a volume that is
/// encrypted but has protection suspended; <see cref="Locked"/> means the contents cannot be read
/// until the volume is unlocked with its password or recovery key.
/// </summary>
public sealed record BitLockerStatus(EncryptionState State, bool ProtectionOn, bool Locked, int? PercentEncrypted = null)
{
    public bool IsConverting => State is EncryptionState.Encrypting or EncryptionState.Decrypting or EncryptionState.Paused;

    public bool IsEncrypted => State is not (EncryptionState.NotEncrypted or EncryptionState.Unknown);
}

/// <summary>A formatted file system that Windows mounted from a partition.</summary>
public sealed record Volume
{
    /// <summary>Volume GUID path, e.g. <c>\\?\Volume{...}\</c>.</summary>
    public required string Path { get; init; }

    public char? DriveLetter { get; init; }

    public string? Label { get; init; }

    /// <summary>File system name such as "NTFS", "exFAT", "FAT32", "ReFS" or empty when unknown.</summary>
    public string FileSystem { get; init; } = string.Empty;

    public ulong SizeBytes { get; init; }

    public ulong FreeBytes { get; init; }

    public VolumeHealth Health { get; init; } = VolumeHealth.Unknown;

    /// <summary>The NTFS dirty bit: set when the volume was not cleanly dismounted or errors were seen; chkdsk clears it.</summary>
    public bool IsDirty { get; init; }

    /// <summary>BitLocker status, or null when it could not be read (no administrator rights, or no BitLocker on this Windows).</summary>
    public BitLockerStatus? BitLocker { get; init; }

    public ulong UsedBytes => SizeBytes >= FreeBytes ? SizeBytes - FreeBytes : 0;

    /// <summary>Fraction of the volume in use, between 0 and 1.</summary>
    public double UsedFraction => SizeBytes == 0 ? 0 : (double)UsedBytes / SizeBytes;
}
