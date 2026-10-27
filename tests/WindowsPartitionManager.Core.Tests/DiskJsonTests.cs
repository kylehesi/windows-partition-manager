using System.Text.Json;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Tests;

public class DiskJsonTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    private static Disk Sample() => new()
    {
        Number = 2,
        FriendlyName = "USB Stick",
        BusType = "USB",
        SizeBytes = 16 * GiB,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 512,
        Style = PartitionStyle.Gpt,
        IsReadOnly = true,
        Partitions =
        [
            new Partition
            {
                Number = 1,
                OffsetBytes = 1 * MiB,
                SizeBytes = 8 * GiB,
                Kind = PartitionKind.Basic,
                TypeDescription = "Basic data",
                DriveLetter = 'F',
                Volume = new Volume { Path = "v", FileSystem = "exFAT", Label = "Stick", SizeBytes = 8 * GiB, FreeBytes = 6 * GiB },
            },
        ],
    };

    [Fact]
    public void Serialize_GivesStableCamelCaseFields_InBytes()
    {
        using var json = JsonDocument.Parse(DiskJson.Serialize([Sample()]));
        var root = json.RootElement;

        Assert.Equal(DiskJson.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        var disk = root.GetProperty("disks")[0];
        Assert.Equal(2, disk.GetProperty("number").GetInt32());
        Assert.Equal("gpt", disk.GetProperty("style").GetString());
        Assert.True(disk.GetProperty("isReadOnly").GetBoolean());
        Assert.Equal(16 * GiB, disk.GetProperty("sizeBytes").GetUInt64());

        var partition = disk.GetProperty("partitions")[0];
        Assert.Equal("F", partition.GetProperty("driveLetter").GetString());
        Assert.Equal("basic", partition.GetProperty("kind").GetString());
        Assert.Equal(2 * GiB, partition.GetProperty("volume").GetProperty("usedBytes").GetUInt64());

        // The free space after the partition, minus the GPT backup area at the end.
        var free = disk.GetProperty("freeRegions")[0];
        Assert.Equal(1 * MiB + (8 * GiB), free.GetProperty("offsetBytes").GetUInt64());
    }

    [Fact]
    public void Serialize_OmitsMissingValues_InsteadOfWritingNull()
    {
        var text = DiskJson.Serialize([Sample() with { Partitions = [], SerialNumber = null }]);

        Assert.DoesNotContain("serialNumber", text, StringComparison.Ordinal);
        Assert.DoesNotContain("null", text, StringComparison.Ordinal);
    }
}
