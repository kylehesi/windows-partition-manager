using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class OperationHistoryTests
{
    private static readonly string[] Log =
    [
        "2026-10-27 17:49:37 +03:00  SetDiskState     disk=1 change=ReadOnly  -> OK (0.6 s)",
        "2026-10-27 17:49:40 +03:00  SetDiskState     disk=1 change=Offline  -> FAILED: SetDiskState failed (0): Refused before touching the disk: The disk is already offline.",
        "garbage from a torn write",
        "2026-10-27 17:50:02 +03:00  CreatePartition  disk=1 offset=1048576 size=1073741824 fs=NTFS letter=auto  -> OK (3.2 s)",
        "",
    ];

    [Fact]
    public void Parse_ReadsEveryWellFormedLine_NewestFirst()
    {
        var records = OperationHistory.Parse(Log);

        Assert.Equal(3, records.Count);
        Assert.Equal("CreatePartition", records[0].Operation);
        Assert.Equal("disk=1 offset=1048576 size=1073741824 fs=NTFS letter=auto", records[0].Details);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 17, 50, 2, TimeSpan.FromHours(3)), records[0].Time);
        Assert.True(records[0].Succeeded);
    }

    [Fact]
    public void Parse_KeepsTheFailureText_AndMarksItFailed()
    {
        var failed = OperationHistory.Parse(Log)[1];

        Assert.False(failed.Succeeded);
        Assert.Equal("disk=1 change=Offline", failed.Details);
        Assert.StartsWith("FAILED: SetDiskState failed (0): Refused", failed.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EmptyLog_IsEmpty()
    {
        Assert.Empty(OperationHistory.Parse([]));
    }
}
