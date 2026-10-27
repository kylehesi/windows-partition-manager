namespace WindowsPartitionManager.Core.Operations;

/// <summary>What an operation does with a volume, as far as BitLocker is concerned.</summary>
public enum EncryptionUse
{
    /// <summary>Shrinks or extends the volume.</summary>
    Resize,

    /// <summary>Works with the contents: check, optimize, rename.</summary>
    Contents,

    /// <summary>Throws the contents away: format, delete.</summary>
    Destroy,
}
