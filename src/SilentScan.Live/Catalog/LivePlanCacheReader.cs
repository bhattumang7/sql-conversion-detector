using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SilentScan.Verify.Oracle;

namespace SilentScan.Live.Catalog;

/// <summary>
/// Turns "this predicate could scan" into "this one is scanning right now": reads the live
/// plan cache (<c>sys.dm_exec_query_stats</c>/<c>sys.dm_exec_query_plan</c>) and runs the same
/// <see cref="ConvertImplicitDetector"/> the Verify oracle uses on each cached plan's XML, so a
/// static finding can be marked as actually observed in a real query plan - with a real
/// execution count - rather than only theoretically possible. Metadata reads only (dynamic
/// management views), same as every other live reader; nothing is ever executed. Requires
/// <c>VIEW SERVER STATE</c> (or <c>VIEW DATABASE STATE</c> at server-scoped level, engine-
/// version-dependent) - a permission a live-mode caller may not have, so a denial is treated as
/// "no evidence available" rather than a hard failure of the rest of the scan.
/// </summary>
public sealed class LivePlanCacheReader
{
    // Scoped to the connected database (qp.dbid = DB_ID()) - the plan cache is instance-wide,
    // and without this filter a table/column name that happens to match one in a completely
    // unrelated database on the same instance would silently contaminate this database's
    // evidence (a real risk on any shared/multi-tenant instance, not a theoretical one - caught
    // while writing this reader's own test, which runs against a shared Docker instance with
    // other test databases live on it at the same time).
    private const string Sql = """
        SELECT TOP (@maxPlans) qs.execution_count, qp.query_plan
        FROM sys.dm_exec_query_stats qs
        CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
        WHERE qp.query_plan IS NOT NULL AND qp.dbid = DB_ID()
        ORDER BY qs.execution_count DESC;
        """;

    // sys.dm_exec_query_plan decodes plan cache entries instance-wide before this query's own
    // WHERE qp.dbid = DB_ID() filter narrows the result set - on a shared instance, another
    // connection concurrently dropping an unrelated database can transiently fail that decode
    // ("Database 'X' is in transition") even though this query never touches that database's
    // data. Retried a few times before degrading to unavailable, since a permission denial (the
    // other realistic failure mode) fails identically on every attempt and still ends up
    // reported the same way once retries are exhausted.
    private const int MaxAttempts = 6;

    private readonly string _connectionString;

    public LivePlanCacheReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Inspects up to <paramref name="maxPlansToInspect"/> cached plans (the busiest first, by
    /// execution count) and returns, per real table column, the total execution count summed
    /// across every distinct cached plan whose XML shows a column-side <c>CONVERT_IMPLICIT</c>
    /// on it. Capped rather than unbounded - a busy production instance's plan cache can hold
    /// tens of thousands of entries, and this is a ranking signal, not an exhaustive audit.
    /// </summary>
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
                    // Most commonly: the connected login lacks VIEW SERVER STATE, or every
                    // retry hit the same transient instance-wide condition. This signal is a
                    // ranking bonus, not a hard requirement of live scanning - degrade to
                    // "unavailable" rather than failing the whole scan-db run over it.
                    return new PlanCacheEvidenceResult([], 0, ex.Message);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }

        throw new UnreachableException("The loop above always returns or throws within its final iteration.");
    }

    private async Task<PlanCacheEvidenceResult> ReadOnceAsync(int maxPlansToInspect, CancellationToken cancellationToken)
    {
        // Keyed by (table, column) while accumulating - a tuple key is convenient here but
        // cannot be System.Text.Json-serialized directly (no built-in converter for a
        // ValueTuple dictionary key), so this is flattened to a plain list before returning.
        var executionCountByColumn = new Dictionary<(string Table, string Column), long>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateReadOnlyCommand(Sql);
        command.Parameters.AddWithValue("@maxPlans", maxPlansToInspect);

        var plansInspected = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plansInspected++;
            AccumulateConversions(reader.GetInt64(0), reader.GetString(1), executionCountByColumn);
        }

        var evidence = executionCountByColumn
            .Select(kvp => new PlanCacheColumnEvidence(kvp.Key.Table, kvp.Key.Column, kvp.Value))
            .OrderByDescending(e => e.ExecutionCount)
            .ThenBy(e => e.TableQualifiedName, StringComparer.Ordinal)
            .ThenBy(e => e.ColumnName, StringComparer.Ordinal)
            .ToList();

        return new PlanCacheEvidenceResult(evidence, plansInspected, UnavailableReason: null);
    }

    private static void AccumulateConversions(
        long executionCount, string planXml, Dictionary<(string Table, string Column), long> executionCountByColumn)
    {
        foreach (var conversion in ConvertImplicitDetector.FindColumnConversions(planXml))
        {
            if (conversion.Table is null)
            {
                continue;
            }

            // TypedPredicateFinding.Column.TableQualifiedName is always schema-qualified
            // ("dbo.Orders") - the plan XML's ColumnReference carries schema and table as
            // separate attributes, so without joining them back together here every lookup
            // against a static finding would silently miss (bare "Orders" never equals
            // "dbo.Orders"), and the plan-cache evidence would look real yet never actually
            // match a single finding.
            var qualifiedTable = conversion.Schema is { Length: > 0 }
                ? $"{conversion.Schema}.{conversion.Table}"
                : conversion.Table;
            var key = (qualifiedTable, conversion.Column ?? string.Empty);
            executionCountByColumn[key] = executionCountByColumn.GetValueOrDefault(key) + executionCount;
        }
    }
}

/// <summary>One real table column, and the total execution count summed across every cached plan whose XML shows it converting.</summary>
public sealed record PlanCacheColumnEvidence(string TableQualifiedName, string ColumnName, long ExecutionCount);

/// <summary>
/// The plan-cache ranking signal's result: per-column observed execution counts (empty when
/// <paramref name="UnavailableReason"/> is set - never silently indistinguishable from "we
/// checked and found nothing").
/// </summary>
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
