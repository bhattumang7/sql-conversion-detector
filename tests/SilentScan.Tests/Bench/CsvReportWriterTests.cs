using SilentScan.Bench.Reporting;
using SilentScan.Bench.Scenarios;

namespace SilentScan.Tests.Bench;

/// <summary>
/// Pins the exact CSV shape the published study's charts read - lowercase booleans and "\n"
/// line endings regardless of platform, not bool.ToString()'s "True"/"False" or
/// AppendLine's platform-dependent Environment.NewLine.
/// </summary>
public sealed class CsvReportWriterTests
{
    [Fact]
    public void Write_SingleResult_ProducesExpectedHeaderAndRow()
    {
        var results = new[]
        {
            new BenchmarkResult("VarCharVsNVarChar_SQL", 10_000, LegacyCardinalityEstimation: true, Matched: false, QuerySelectivity.SingleRow, 1234, 56, 78),
        };

        var csv = CsvReportWriter.Write(results);

        Assert.Equal(
            "ScenarioName,RowCount,LegacyCardinalityEstimation,Matched,Selectivity,MedianLogicalReads,MedianCpuMs,MedianElapsedMs\n"
            + "VarCharVsNVarChar_SQL,10000,true,false,SingleRow,1234,56,78\n",
            csv);
    }

    [Fact]
    public void Write_NoResults_ProducesHeaderOnly()
    {
        var csv = CsvReportWriter.Write([]);

        Assert.Equal("ScenarioName,RowCount,LegacyCardinalityEstimation,Matched,Selectivity,MedianLogicalReads,MedianCpuMs,MedianElapsedMs\n", csv);
    }

    [Fact]
    public void Write_NeverEmitsCarriageReturn()
    {
        var results = new[]
        {
            new BenchmarkResult("Scenario", 1, true, true, QuerySelectivity.SingleRow, 1, 1, 1),
            new BenchmarkResult("Scenario", 2, false, false, QuerySelectivity.OnePercent, 2, 2, 2),
        };

        var csv = CsvReportWriter.Write(results);

        Assert.DoesNotContain('\r', csv);
    }

    [Fact]
    public void Write_MultipleResults_EachOnItsOwnLine()
    {
        var results = new[]
        {
            new BenchmarkResult("A", 1, true, true, QuerySelectivity.SingleRow, 1, 1, 1),
            new BenchmarkResult("B", 2, false, false, QuerySelectivity.TenPercent, 2, 2, 2),
        };

        var csv = CsvReportWriter.Write(results);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("A,1,true,true,SingleRow", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("B,2,false,false,TenPercent", lines[2], StringComparison.Ordinal);
    }
}
