using SilentScan.Bench.Execution;
using SilentScan.Bench.Reporting;
using SilentScan.Bench.Scenarios;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

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

        Assert.Equal(12, results.Count);

        foreach (var legacyCe in new[] { true, false })
        {
            foreach (var selectivity in new[] { QuerySelectivity.SingleRow, QuerySelectivity.OnePercent, QuerySelectivity.TenPercent })
            {
                var matched = results.Single(r => r.LegacyCardinalityEstimation == legacyCe && r.Matched && r.Selectivity == selectivity);
                var mismatched = results.Single(r => r.LegacyCardinalityEstimation == legacyCe && !r.Matched && r.Selectivity == selectivity);

                Assert.True(
                    mismatched.MedianLogicalReads > matched.MedianLogicalReads,
                    $"Expected mismatched logical reads ({mismatched.MedianLogicalReads}) to exceed matched ({matched.MedianLogicalReads}) under legacyCe={legacyCe}, selectivity={selectivity}.");
            }
        }
    }

    [Fact]
    public async Task RunAsync_RangeSelectivity_ReadsScaleWithBandSizeNotJustPresenceOfAConversion()
    {
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
