using Microsoft.Data.SqlClient;
using SilentScan.Bench.Reporting;
using SilentScan.Bench.Scenarios;
using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Core.TypeInference;

namespace SilentScan.Bench.Execution;

/// <summary>
/// Runs the full CLAUDE.md Benchmark protocol matrix: per scenario x row count x CE mode x
/// matched/mismatched x selectivity, median-of-5 warm runs. Compat level pinned to 160 (via
/// DatabaseProvisioner), MAXDOP 1 for reproducibility. The selectivity dimension (an audit
/// finding) exists because a single-row equality probe can't tell a genuine `RangeSeek` apart
/// from a `ScanForced` one on cost alone - both touch one row regardless of which plan shape
/// `GetRangeThroughConvert` produces; only a multi-row range predicate exercises that
/// machinery at a scale where its overhead (or the scan's) actually shows up.
/// </summary>
public sealed class BenchmarkRunner(SqlServerOptions options)
{
    private const int WarmRuns = 1;
    private const int TimedRuns = 5;

    private static readonly IReadOnlyList<QuerySelectivity> Selectivities =
        [QuerySelectivity.SingleRow, QuerySelectivity.OnePercent, QuerySelectivity.TenPercent];

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(
        string databaseName,
        IReadOnlyList<TypePairScenario> scenarios,
        IReadOnlyList<int> rowCounts,
        CancellationToken cancellationToken = default)
    {
        var provisioner = new DatabaseProvisioner(options);
        await provisioner.CreateFreshAsync(databaseName, cancellationToken: cancellationToken);
        try
        {
            await using var connection = new SqlConnection(options.BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);
            await SetMaxDopAsync(connection, cancellationToken);
            await EnableStatisticsCaptureAsync(connection, cancellationToken);

            var results = new List<BenchmarkResult>();
            foreach (var scenario in scenarios)
            {
                foreach (var rowCount in rowCounts)
                {
                    var tableName = $"Bench_{scenario.Name}_{rowCount}";
                    await SyntheticTableSeeder.SeedAsync(connection, scenario, tableName, rowCount, cancellationToken);

                    foreach (var legacyCe in new[] { true, false })
                    {
                        await SetLegacyCardinalityEstimationAsync(connection, legacyCe, cancellationToken);

                        foreach (var selectivity in Selectivities)
                        {
                            results.Add(await RunCellAsync(connection, new BenchmarkCell(scenario, tableName, rowCount, legacyCe, Matched: true, selectivity), cancellationToken));
                            results.Add(await RunCellAsync(connection, new BenchmarkCell(scenario, tableName, rowCount, legacyCe, Matched: false, selectivity), cancellationToken));
                        }
                    }
                }
            }

            return results;
        }
        finally
        {
            // Otherwise a 10M-row synthetic benchmark database persists on disk indefinitely -
            // every other disposable-database consumer in this codebase (Verify, TypeMatrixGenerator)
            // drops in a finally; this was the one that didn't.
            await provisioner.DropIfExistsAsync(databaseName, cancellationToken);
        }
    }

    private static async Task<BenchmarkResult> RunCellAsync(SqlConnection connection, BenchmarkCell cell, CancellationToken cancellationToken)
    {
        var paramTypeDdl = cell.Matched ? cell.Scenario.MatchedParamTypeDdl : cell.Scenario.MismatchedParamTypeDdl;
        var query = cell.Selectivity.Fraction() is { } fraction
            ? BuildRangeQuery(cell.Scenario, cell.TableName, cell.RowCount, cell.Matched, paramTypeDdl, fraction)
            : BuildSingleRowQuery(cell.Scenario, cell.TableName, cell.RowCount, cell.Matched, paramTypeDdl);

        for (var i = 0; i < WarmRuns; i++)
        {
            await StatisticsCapture.CaptureAsync(connection, query, cancellationToken);
        }

        var runs = new List<QueryStatistics>();
        for (var i = 0; i < TimedRuns; i++)
        {
            runs.Add(await StatisticsCapture.CaptureAsync(connection, query, cancellationToken));
        }

        return new BenchmarkResult(
            cell.Scenario.Name,
            cell.RowCount,
            cell.LegacyCardinalityEstimation,
            cell.Matched,
            cell.Selectivity,
            Median(runs.Select(r => r.LogicalReads)),
            Median(runs.Select(r => r.CpuMs)),
            Median(runs.Select(r => r.ElapsedMs)),
            StaticVerdict(cell.Scenario, cell.Matched));
    }

    /// <summary>
    /// What <see cref="VerdictClassifier"/> itself predicts for this cell's exact type pair - a
    /// Matched row is always SeekPreserved by construction (its param IS the column's own type/
    /// collation, never reclassified), never a guess; a Mismatched row is classified for real,
    /// reusing the same code path the scan pipeline uses rather than duplicating its logic.
    /// </summary>
    private static Verdict StaticVerdict(TypePairScenario scenario, bool matched)
    {
        if (matched)
        {
            return Verdict.SeekPreserved;
        }

        var columnType = new SqlType(scenario.ColumnCategory, Collation: scenario.Collation);
        var otherType = new SqlType(scenario.MismatchedOtherCategory);
        return VerdictClassifier.Classify(columnType, otherType, otherIsLiteral: false, operatorText: "=");
    }

    /// <summary>One cell's identity within the benchmark matrix - bundled so <see cref="RunCellAsync"/> stays under the parameter-count limit as the matrix grows another dimension.</summary>
    private sealed record BenchmarkCell(TypePairScenario Scenario, string TableName, int RowCount, bool LegacyCardinalityEstimation, bool Matched, QuerySelectivity Selectivity);

    // A local `DECLARE @p ... = value` is never sniffed - the optimizer treats it as an
    // unknown-at-compile-time value and falls back to a density-vector estimate regardless of
    // CE mode, so the legacy-vs-modern CE dimension of the benchmark matrix measured almost
    // nothing. Routing through sp_executesql with parameters passed as genuine parameters
    // compiles the inner statement with a real, sniffable parameterized plan - the same shape
    // the study's ORM-generated-query motivation is actually about.
    private static string BuildSingleRowQuery(TypePairScenario scenario, string tableName, int rowCount, bool matched, string paramTypeDdl)
    {
        var probeRow = rowCount / 2;
        var paramValue = matched ? scenario.MatchedParamValueForRow(probeRow) : scenario.MismatchedParamValueForRow(probeRow);

        var innerStatement = $"SELECT Id FROM dbo.{tableName} WHERE Code = @p OPTION (MAXDOP 1);";
        return $"EXEC sp_executesql N'{innerStatement.Replace("'", "''", StringComparison.Ordinal)}', " +
            $"N'@p {paramTypeDdl}', @p = {paramValue};";
    }

    /// <summary>
    /// A contiguous band of rows centered on the table's midpoint, covering
    /// <paramref name="fraction"/> of <paramref name="rowCount"/> - reuses the scenario's own
    /// per-row value functions for the bounds (the same functions that produce the seed data
    /// and the single-row probe value), so a range bound is guaranteed to be a value that
    /// actually exists in the seeded table, in the exact same type representation the
    /// single-row case already uses.
    /// </summary>
    private static string BuildRangeQuery(TypePairScenario scenario, string tableName, int rowCount, bool matched, string paramTypeDdl, double fraction)
    {
        var bandSize = Math.Max(1, (int)(rowCount * fraction));
        var startRow = Math.Max(0, (rowCount - bandSize) / 2);
        var endRow = Math.Min(rowCount - 1, startRow + bandSize);

        var lowerValue = matched ? scenario.MatchedParamValueForRow(startRow) : scenario.MismatchedParamValueForRow(startRow);
        var upperValue = matched ? scenario.MatchedParamValueForRow(endRow) : scenario.MismatchedParamValueForRow(endRow);

        var innerStatement = $"SELECT Id FROM dbo.{tableName} WHERE Code >= @lo AND Code < @hi OPTION (MAXDOP 1);";
        return $"EXEC sp_executesql N'{innerStatement.Replace("'", "''", StringComparison.Ordinal)}', " +
            $"N'@lo {paramTypeDdl}, @hi {paramTypeDdl}', @lo = {lowerValue}, @hi = {upperValue};";
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    private static async Task SetMaxDopAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnableStatisticsCaptureAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS IO ON; SET STATISTICS TIME ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetLegacyCardinalityEstimationAsync(SqlConnection connection, bool enabled, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE SCOPED CONFIGURATION SET LEGACY_CARDINALITY_ESTIMATION = {(enabled ? "ON" : "OFF")};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
