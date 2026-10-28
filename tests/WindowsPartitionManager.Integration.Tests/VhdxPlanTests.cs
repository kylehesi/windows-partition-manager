using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Layout;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;
using WindowsPartitionManager.Integration.Tests.Support;
using Xunit.Abstractions;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>A plan of several steps, built on paper and then applied by Windows, must end exactly as projected.</summary>
[SupportedOSPlatform("windows")]
public sealed class VhdxPlanTests(ITestOutputHelper output)
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [AdminFact]
    public async Task CreateFormatShrinkCreateDelete_EndsAsProjected_AndKeepsTheData()
    {
        using var vhd = TestVhd.Create(2 * GiB, output, "plan");
        var ops = vhd.Operations;
        var n = vhd.DiskNumber;

        await vhd.WaitAsync(_ => true);
        await ops.InitializeDiskAsync(n, PartitionStyle.Gpt);
        var disk = await vhd.WaitAsync(d => d.Style == PartitionStyle.Gpt);

        // A partition that already holds data, so the plan has something real to shrink.
        var start = FreeSpaceCalculator.Compute(disk)[0].OffsetBytes;
        var a = await ops.CreatePartitionAsync(new CreatePartitionRequest { DiskNumber = n, OffsetBytes = start, SizeBytes = 768 * MiB, FileSystem = "NTFS", Label = "A" });
        await File.WriteAllTextAsync($@"{a.DriveLetter}:\keep.txt", "stays");
        disk = await vhd.WaitAsync(d => d.Partitions.Any(p => p.Number == a.Number && p.Volume is not null));
        output.WriteLine("Start:     " + TestVhd.LayoutOf(disk));

        var plan = new OperationPlan(await vhd.Provider.GetDisksAsync());
        void Add(PlannedOperation step)
        {
            var issues = plan.TryAdd(step);
            Assert.DoesNotContain(issues, i => i.IsError);
            output.WriteLine($"Planned:   {plan.Steps[^1].Description}");
        }

        var bStart = a.EndBytes;
        Add(new PlannedCreate(n, bStart, 256 * MiB, "NTFS", "B", null));
        Add(new PlannedFormat(n, bStart, "exFAT", "B2"));
        Add(new PlannedResize(n, a.OffsetBytes, 512 * MiB));
        Add(new PlannedCreate(n, a.OffsetBytes + (512 * MiB), 256 * MiB, "NTFS", "C", null));
        Add(new PlannedDelete(n, bStart));
        var projected = plan.Projected.Single(d => d.Number == n);
        output.WriteLine("Projected: " + TestVhd.LayoutOf(projected));

        var result = await PlanRunner.RunAsync(await vhd.Provider.GetDisksAsync(), [.. plan.Steps.Select(s => s.Operation)], vhd.Provider, ops, new Progress<PlanProgress>(p => output.WriteLine($"  [{p.Step}/{p.TotalSteps}] {p.Detail ?? p.Description}")));
        Assert.True(result.Succeeded, result.Error);

        var final = await vhd.WaitAsync(d => d.Partitions.Count == projected.Partitions.Count);
        output.WriteLine("Final:     " + TestVhd.LayoutOf(final));
        Assert.Null(PlanRunner.Difference(final, projected));
        Assert.Equal("stays", await File.ReadAllTextAsync($@"{a.DriveLetter}:\keep.txt"));
        Assert.Equal(0, vhd.Chkdsk(a.DriveLetter!.Value));
    }
}
