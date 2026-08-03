using SilentScan.Bench.Execution;
using SilentScan.Bench.Reporting;
using SilentScan.Bench.Scenarios;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Phase 5 exit criterion (plan.md): "the cost table that the writeup charts." Runs the real
/// benchmark harness against the live Docker SQL Server oracle at a small row count (full
/// 10K/1M/10M runs are a CLI operation, not part of the automated suite - inserting and
/// benchmarking 10M rows several times over would make `dotnet test` impractically slow).
/// The harness itself is unchanged between scales; what's verified here is that it produces
/// a real, meaningful signal: the mismatched (implicit-conversion) query costs measurably
/// more than the matched one.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class BenchmarkRunnerTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanBenchTest";
    private const int RowCount = 2_000;

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() =>
        await new DatabaseProvisioner(_options).DropIfExistsAsync(DatabaseName);

    [Fact]
    public async Task RunAsync_VarCharVsNVarChar_MismatchedCostsMoreThanMatched()
    {
        var scenario = TypePairScenario.VarCharVsNVarChar("SQL_Latin1_General_CP1_CI_AS");
        var runner = new BenchmarkRunner(_options);

        var results = await runner.RunAsync(DatabaseName, [scenario], [RowCount]);

        // 12 cells: matched/mismatched x legacy CE on/off x 3 selectivities (SingleRow/
        // OnePercent/TenPercent).
        Assert.Equal(12, results.Count);

        foreach (var legacyCe in new[] { true, false })
        {
            foreach (var selectivity in new[] { QuerySelectivity.SingleRow, QuerySelectivity.OnePercent, QuerySelectivity.TenPercent })
            {
                var matched = results.Single(r => r.LegacyCardinalityEstimation == legacyCe && r.Matched && r.Selectivity == selectivity);
                var mismatched = results.Single(r => r.LegacyCardinalityEstimation == legacyCe && !r.Matched && r.Selectivity == selectivity);

                // SQL_* collation forces a scan on mismatch; a scan touches every row's worth of
                // pages, so its logical reads must be at least as high as a seek's - and for a
                // scenario shaped this way, meaningfully higher, at every selectivity tested.
                Assert.True(
                    mismatched.MedianLogicalReads > matched.MedianLogicalReads,
                    $"Expected mismatched logical reads ({mismatched.MedianLogicalReads}) to exceed matched ({matched.MedianLogicalReads}) under legacyCe={legacyCe}, selectivity={selectivity}.");
            }
        }
    }

    [Fact]
    public async Task RunAsync_RangeSelectivity_ReadsScaleWithBandSizeNotJustPresenceOfAConversion()
    {
        // The audit finding this exists to close: a single-row probe can't tell a genuine
        // RangeSeek apart from a ScanForced verdict on cost alone. Under a Windows collation
        // (RangeSeek-capable), reads at TenPercent selectivity should exceed reads at
        // OnePercent for the SAME mismatched comparison - proving the range dimension actually
        // varies the amount of data touched, not just whether a conversion is present at all.
        // Needs a larger table than the other tests here: the index on Code already covers
        // Id (a nonclustered index's leaf level implicitly carries the clustering key), so at
        // RowCount's usual small scale both a 1% and a 10% band fit on the same one or two
        // leaf pages and genuinely read identically - this only becomes visible once the
        // table is large enough for the two band sizes to span a different number of pages.
        const int largeRowCount = 200_000;
        var scenario = TypePairScenario.VarCharVsNVarChar("Latin1_General_CI_AS");
        var runner = new BenchmarkRunner(_options);

        var results = await runner.RunAsync(DatabaseName, [scenario], [largeRowCount]);

        var onePercent = results.Single(r => !r.Matched && !r.LegacyCardinalityEstimation && r.Selectivity == QuerySelectivity.OnePercent);
        var tenPercent = results.Single(r => !r.Matched && !r.LegacyCardinalityEstimation && r.Selectivity == QuerySelectivity.TenPercent);

        Assert.True(
            tenPercent.MedianLogicalReads > onePercent.MedianLogicalReads,
            $"Expected TenPercent logical reads ({tenPercent.MedianLogicalReads}) to exceed OnePercent ({onePercent.MedianLogicalReads}).");
    }

    [Fact]
    public async Task RunAsync_ResultsWriteToValidCsv()
    {
        var scenario = TypePairScenario.IntVsBigInt();
        var runner = new BenchmarkRunner(_options);

        var results = await runner.RunAsync(DatabaseName, [scenario], [RowCount]);
        var csv = CsvReportWriter.Write(results);
        var lines = csv.TrimEnd().Split('\n');

        Assert.Equal("ScenarioName,RowCount,LegacyCardinalityEstimation,Matched,Selectivity,MedianLogicalReads,MedianCpuMs,MedianElapsedMs,StaticVerdict", lines[0].TrimEnd('\r'));
        Assert.Equal(results.Count + 1, lines.Length);
    }

    [Fact]
    public async Task RunAsync_StampsTheStaticVerdictVerdictClassifierPredictsForEachCell()
    {
        // The task this exists for: a bare Matched=false row gives no way to tell "this row
        // confirms the classifier" from "this row contradicts it" without cross-referencing the
        // matrix by hand. Every row must now carry that answer directly - a Matched row is
        // trivially SeekPreserved (its param IS the column's own type), and each collation
        // family's Mismatched row must carry the verdict VerdictClassifier itself predicts for
        // that exact pair (SQL_* -> ScanForced, Windows -> RangeSeek).
        var sqlFamily = TypePairScenario.VarCharVsNVarChar("SQL_Latin1_General_CP1_CI_AS");
        var windowsFamily = TypePairScenario.VarCharVsNVarChar("Latin1_General_CI_AS");
        var runner = new BenchmarkRunner(_options);

        var results = await runner.RunAsync(DatabaseName, [sqlFamily, windowsFamily], [RowCount]);

        Assert.All(results.Where(r => r.Matched), r => Assert.Equal(SilentScan.Core.Rules.Verdict.SeekPreserved, r.StaticVerdict));
        Assert.All(
            results.Where(r => !r.Matched && r.ScenarioName == sqlFamily.Name),
            r => Assert.Equal(SilentScan.Core.Rules.Verdict.ScanForced, r.StaticVerdict));
        Assert.All(
            results.Where(r => !r.Matched && r.ScenarioName == windowsFamily.Name),
            r => Assert.Equal(SilentScan.Core.Rules.Verdict.RangeSeek, r.StaticVerdict));
    }
}
