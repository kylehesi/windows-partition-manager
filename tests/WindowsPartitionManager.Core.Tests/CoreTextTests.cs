using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using WindowsPartitionManager.Core.Localization;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

/// <summary>
/// <see cref="CoreText.Culture"/> is process-wide and the other test classes expect English
/// messages, so the tests that switch it run on their own, after the parallel ones.
/// </summary>
[CollectionDefinition(nameof(CoreTextCollection), DisableParallelization = true)]
public sealed class CoreTextCollection;

[Collection(nameof(CoreTextCollection))]
public class CoreTextTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static readonly ResourceManager Manager =
        new("WindowsPartitionManager.Core.Localization.CoreStrings", typeof(CoreText).Assembly);

    private static Disk GptDisk(params Partition[] partitions) => new()
    {
        Number = 1,
        FriendlyName = "Test",
        BusType = "SATA",
        SizeBytes = 100 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        Style = PartitionStyle.Gpt,
        Partitions = partitions,
    };

    private static Partition Ntfs(ulong size) => new()
    {
        Number = 1,
        OffsetBytes = 1 * MiB,
        SizeBytes = size,
        Kind = PartitionKind.Basic,
        TypeDescription = "Basic data",
        DriveLetter = 'D',
        Volume = new Volume { Path = "v", FileSystem = "NTFS", SizeBytes = size, FreeBytes = size / 2, DriveLetter = 'D' },
    };

    private static IReadOnlyList<string> SameSizeMessages()
    {
        var partition = Ntfs(50 * GiB);
        var request = new ResizePartitionRequest { DiskNumber = 1, PartitionNumber = 1, NewSizeBytes = partition.SizeBytes };
        return ResizePlanner.Validate(GptDisk(partition), partition, request).Select(i => i.Message).ToList();
    }

    [Fact]
    public void Default_IsEnglish()
    {
        Assert.Contains("The partition already has that size.", SameSizeMessages());
        Assert.Equal("Basic data", CoreText.PartitionType("Basic data"));
    }

    [Fact]
    public void Turkish_TranslatesPlannerMessagesAndPartitionTypes()
    {
        try
        {
            CoreText.Culture = CultureInfo.GetCultureInfo("tr");
            Assert.Contains("Bölüm zaten bu boyutta.", SameSizeMessages());
            Assert.Equal("Temel veri", CoreText.PartitionType("Basic data"));
            Assert.Equal("Disk 3 çıkarılabilir bir USB aygıtı. İşlem bitene kadar çıkarmayın.", CoreText.Format("Safety_Removable", 3, "USB"));
        }
        finally
        {
            CoreText.Culture = CultureInfo.InvariantCulture;
        }
    }

    [Fact]
    public void PartitionType_WithoutTranslation_IsShownAsItIs()
    {
        try
        {
            CoreText.Culture = CultureInfo.GetCultureInfo("tr");
            Assert.Equal("MBR type 0x1C", CoreText.PartitionType("MBR type 0x1C"));
        }
        finally
        {
            CoreText.Culture = CultureInfo.InvariantCulture;
        }
    }

    [Fact]
    public void TurkishTable_HasEveryKeyWithTheSamePlaceholders()
    {
        var english = Read(CultureInfo.InvariantCulture);
        var turkish = Read(CultureInfo.GetCultureInfo("tr"));

        Assert.Equal(english.Keys.Order(), turkish.Keys.Order());
        foreach (var (key, text) in english)
        {
            Assert.True(Placeholders(text).SetEquals(Placeholders(turkish[key])), $"{key}: placeholders differ");
            _ = string.Format(CultureInfo.InvariantCulture, turkish[key], new object[10]);
        }
    }

    private static Dictionary<string, string> Read(CultureInfo culture)
    {
        var set = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static HashSet<string> Placeholders(string text)
        => Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).ToHashSet();
}
