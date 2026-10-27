using System.Globalization;
using WindowsPartitionManager.App.ViewModels;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.App.Tests;

public sealed class EncryptionTextTests
{
    [Fact]
    public void EachBitLockerState_HasItsOwnWords_AndUnencryptedHasNone()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

        Assert.Equal(string.Empty, SegmentRow.DescribeEncryption(null));
        Assert.Equal(string.Empty, SegmentRow.DescribeEncryption(new BitLockerStatus(EncryptionState.NotEncrypted, false, false)));
        Assert.Contains("protection on", SegmentRow.DescribeEncryption(new BitLockerStatus(EncryptionState.Encrypted, true, false)), StringComparison.Ordinal);
        Assert.Contains("protection off", SegmentRow.DescribeEncryption(new BitLockerStatus(EncryptionState.Encrypted, false, false)), StringComparison.Ordinal);
        Assert.Contains("locked", SegmentRow.DescribeEncryption(new BitLockerStatus(EncryptionState.Encrypted, true, true)), StringComparison.Ordinal);
        Assert.Contains("37 %", SegmentRow.DescribeEncryption(new BitLockerStatus(EncryptionState.Encrypting, true, false, 37)), StringComparison.Ordinal);
    }
}
