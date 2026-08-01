using System.Globalization;
using System.Text;

namespace SilentScan.Bench.Reporting;

/// <summary>Writes the cost table CSV (CLAUDE.md: "Output a CSV the writeup can chart directly").</summary>
public static class CsvReportWriter
{
    public static string Write(IReadOnlyList<BenchmarkResult> results)
    {
        // Explicit "\n" (not AppendLine's platform-dependent Environment.NewLine) and
        // lowercase booleans (not bool.ToString()'s "True"/"False") so the CSV is byte-
        // identical regardless of what platform generated it - this file feeds a published
        // study's charts, not just a human reading it in a terminal.
        var builder = new StringBuilder();
        builder.Append("ScenarioName,RowCount,LegacyCardinalityEstimation,Matched,Selectivity,MedianLogicalReads,MedianCpuMs,MedianElapsedMs\n");

        foreach (var r in results)
        {
            builder.Append(string.Join(',',
                r.ScenarioName,
                r.RowCount.ToString(CultureInfo.InvariantCulture),
                r.LegacyCardinalityEstimation ? "true" : "false",
                r.Matched ? "true" : "false",
                r.Selectivity.ToString(),
                r.MedianLogicalReads.ToString(CultureInfo.InvariantCulture),
                r.MedianCpuMs.ToString(CultureInfo.InvariantCulture),
                r.MedianElapsedMs.ToString(CultureInfo.InvariantCulture)));
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
