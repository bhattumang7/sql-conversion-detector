using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Oracle;

namespace SilentScan.Live.Catalog;

public sealed class LivePlanCacheReader
{

    private const string Sql = """
        WITH FilteredPlans AS (
            SELECT DISTINCT qs.plan_handle, qs.execution_count
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) epa
            WHERE epa.attribute = 'dbid' AND CONVERT(int, epa.value) = DB_ID()
        )
        SELECT TOP (@maxPlans) fp.execution_count, qp.query_plan
        FROM FilteredPlans fp
        CROSS APPLY sys.dm_exec_query_plan(fp.plan_handle) qp
        WHERE qp.query_plan IS NOT NULL
        ORDER BY fp.execution_count DESC;
        """;

    private const int MaxAttempts = 6;

    private readonly string _connectionString;

    public LivePlanCacheReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<PlanCacheEvidenceResult> ReadObservedConversionsAsync(
        int maxPlansToInspect = 1000, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await ReadOnceAsync(maxPlansToInspect, cancellationToken);
            }
            catch (SqlException ex)
            {
                if (attempt == MaxAttempts)
                {

                    return new PlanCacheEvidenceResult([], 0, ex.Message);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }

        throw new UnreachableException("The loop above always returns or throws within its final iteration.");
    }

    private async Task<PlanCacheEvidenceResult> ReadOnceAsync(int maxPlansToInspect, CancellationToken cancellationToken)
    {
        var accumulated = await AccumulateAsync(maxPlansToInspect, cancellationToken);

        var evidence = accumulated.ByColumn
            .Select(kvp => new PlanCacheColumnEvidence(kvp.Key.Table, kvp.Key.Column, kvp.Value.ExecutionCount))
            .OrderByDescending(e => e.ExecutionCount)
            .ThenBy(e => e.TableQualifiedName, StringComparer.Ordinal)
            .ThenBy(e => e.ColumnName, StringComparer.Ordinal)
            .ToList();

        return new PlanCacheEvidenceResult(evidence, accumulated.PlansInspected, UnavailableReason: null);
    }

    public async Task<IReadOnlyList<WorkloadFinding>> ReadWorkloadFindingsAsync(
        DatabaseCatalog catalog, IReadOnlySet<(string TableQualifiedName, string ColumnName)> alreadyCoveredColumns,
        int maxPlansToInspect = 1000, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var accumulated = await AccumulateAsync(maxPlansToInspect, cancellationToken);
                return accumulated.ByColumn
                    .Where(kvp => !alreadyCoveredColumns.Contains(kvp.Key))
                    .Select(kvp => ToWorkloadFinding(kvp.Key.Table, kvp.Key.Column, kvp.Value, catalog))
                    .OrderByDescending(f => f.ExecutionCount)
                    .ThenBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                    .ThenBy(f => f.ColumnName, StringComparer.Ordinal)
                    .ToList();
            }
            catch (SqlException)
            {

                if (attempt == MaxAttempts)
                {
                    return [];
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }

        throw new UnreachableException("The loop above always returns within its final iteration.");
    }

    private static WorkloadFinding ToWorkloadFinding(string table, string column, ColumnAccumulation accumulation, DatabaseCatalog catalog)
    {
        var indexed = catalog.Find(table)?.IsIndexedColumn(column) ?? false;
        var verdict = accumulation.HasRangeSeek ? WorkloadVerdict.RangeSeek : WorkloadVerdict.ScanForced;
        return new WorkloadFinding(table, column, indexed, verdict, accumulation.ExecutionCount);
    }

    private async Task<(Dictionary<(string Table, string Column), ColumnAccumulation> ByColumn, int PlansInspected)> AccumulateAsync(
        int maxPlansToInspect, CancellationToken cancellationToken)
    {

        var byColumn = new Dictionary<(string Table, string Column), ColumnAccumulation>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateReadOnlyCommand(Sql);
        command.Parameters.AddWithValue("@maxPlans", maxPlansToInspect);

        var plansInspected = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plansInspected++;
            AccumulateConversions(reader.GetInt64(0), reader.GetString(1), byColumn);
        }

        return (byColumn, plansInspected);
    }

    private static void AccumulateConversions(
        long executionCount, string planXml, Dictionary<(string Table, string Column), ColumnAccumulation> byColumn)
    {
        foreach (var conversion in ConvertImplicitDetector.FindColumnConversions(planXml))
        {
            if (conversion.Table is null)
            {
                continue;
            }

            var qualifiedTable = conversion.Schema is { Length: > 0 }
                ? $"{conversion.Schema}.{conversion.Table}"
                : conversion.Table;
            var key = (qualifiedTable, conversion.Column ?? string.Empty);
            var existing = byColumn.GetValueOrDefault(key);
            byColumn[key] = new ColumnAccumulation(
                existing.ExecutionCount + executionCount, existing.HasRangeSeek || conversion.RangeSeekBound);
        }
    }

    private readonly record struct ColumnAccumulation(long ExecutionCount, bool HasRangeSeek);
}

public enum WorkloadVerdict
{
    ScanForced,
    RangeSeek,
}

public sealed record WorkloadFinding(string TableQualifiedName, string ColumnName, bool Indexed, WorkloadVerdict Verdict, long ExecutionCount);

public sealed record PlanCacheColumnEvidence(string TableQualifiedName, string ColumnName, long ExecutionCount);

public sealed record PlanCacheEvidenceResult(
    IReadOnlyList<PlanCacheColumnEvidence> ColumnEvidence,
    int PlansInspected,
    string? UnavailableReason)
{
    public bool TryGetExecutionCount(string tableQualifiedName, string columnName, out long executionCount)
    {
        var match = ColumnEvidence.FirstOrDefault(entry =>
            string.Equals(entry.TableQualifiedName, tableQualifiedName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));

        executionCount = match?.ExecutionCount ?? 0;
        return match is not null;
    }
}
