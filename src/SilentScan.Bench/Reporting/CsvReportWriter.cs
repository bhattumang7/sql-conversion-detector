using System.Globalization;
using System.Text;

namespace SilentScan.Bench.Reporting;

public static class CsvReportWriter
{
    public static string Write(IReadOnlyList<BenchmarkResult> results)
    {

        var builder = new StringBuilder();
        builder.Append("ScenarioName,RowCount,LegacyCardinalityEstimation,Matched,Selectivity,MedianLogicalReads,MedianCpuMs,MedianElapsedMs,StaticVerdict\n");

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
                r.MedianElapsedMs.ToString(CultureInfo.InvariantCulture),
                r.StaticVerdict.ToString()));
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
