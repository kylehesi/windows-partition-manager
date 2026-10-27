using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>One line of the operation log: what was asked of Windows, when, and how it ended.</summary>
public sealed record OperationRecord(DateTimeOffset Time, string Operation, string Details, string Outcome)
{
    public bool Succeeded => Outcome.StartsWith("OK", StringComparison.Ordinal);
}

/// <summary>
/// Reads the operation log back. Each line is
/// <c>yyyy-MM-dd HH:mm:ss zzz  Operation  details  -&gt; outcome</c>; lines that do not have that
/// shape (a torn write after a crash, a hand edit) are skipped rather than failing the whole log.
/// </summary>
public static partial class OperationHistory
{
    public static IReadOnlyList<OperationRecord> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var records = new List<OperationRecord>();
        foreach (var line in lines)
        {
            var match = LinePattern().Match(line);
            if (!match.Success
                || !DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                continue;
            }

            records.Add(new OperationRecord(time, match.Groups["op"].Value, match.Groups["details"].Value.Trim(), match.Groups["outcome"].Value.Trim()));
        }

        // Newest first: the last thing that happened is what the user is looking for.
        records.Reverse();
        return records;
    }

    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2})\s+(?<op>\S+)\s+(?<details>.*?)\s+-> (?<outcome>.*)$")]
    private static partial Regex LinePattern();
}
