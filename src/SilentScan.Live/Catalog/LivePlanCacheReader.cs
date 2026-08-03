using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
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
    // Scoped to the connected database, but NOT via qp.dbid (sys.dm_exec_query_plan's own dbid
    // column) - that column only exists on the fully-decoded plan, so a naive
    // "CROSS APPLY sys.dm_exec_query_plan(...) WHERE qp.dbid = DB_ID()" still forces the engine
    // to decode EVERY cached plan instance-wide before the filter can discard the ones that
    // don't match. On a shared instance, another connection concurrently dropping an unrelated
    // database can transiently fail that decode ("Database 'X' is in transition") for a plan
    // this query was always going to throw away anyway.
    //
    // sys.dm_exec_plan_attributes(plan_handle) exposes the same dbid as a cheap key/value
    // attribute read off the plan cache entry itself, without decoding the plan XML at all
    // (verified directly against the live Docker instance: the 'dbid' attribute is present and
    // correct for every cached plan, including proc-object plans compiled in a specific
    // database). Filtering FilteredPlans down to this database's own plan handles first, and
    // only THEN invoking sys.dm_exec_query_plan on that already-narrowed set, means the fragile
    // decode is never attempted on a plan belonging to some other, possibly-currently-dropping
    // database in the first place - the failure mode is structurally unreachable, not just
    // retried around.
    private const string Sql = """
        WITH FilteredPlans AS (
            SELECT DISTINCT qs.plan_handle, qs.execution_count
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) epa
            WHERE epa.attribute = 'dbid' AND TRY_CONVERT(int, epa.value) = DB_ID()
        )
        SELECT TOP (@maxPlans) fp.execution_count, qp.query_plan
        FROM FilteredPlans fp
        CROSS APPLY sys.dm_exec_query_plan(fp.plan_handle) qp
        WHERE qp.query_plan IS NOT NULL
        ORDER BY fp.execution_count DESC;
        """;

    // The cross-database decode race the comment above describes is now structurally
    // unreachable, but a login can still lack VIEW SERVER STATE (a permission denial, which
    // fails identically on every attempt), and sys.dm_exec_query_plan can still fail to decode a
    // plan belonging to THIS OWN database's current DDL churn in a corpus-scanning run (this
    // reader's own test suite creates/drops databases around it). Kept as a safety net for that
    // narrower residual case, not as a substitute for the structural fix above.
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
        var accumulated = await AccumulateAsync(maxPlansToInspect, cancellationToken);

        var evidence = accumulated.ByColumn
            .Select(kvp => new PlanCacheColumnEvidence(kvp.Key.Table, kvp.Key.Column, kvp.Value.ExecutionCount))
            .OrderByDescending(e => e.ExecutionCount)
            .ThenBy(e => e.TableQualifiedName, StringComparer.Ordinal)
            .ThenBy(e => e.ColumnName, StringComparer.Ordinal)
            .ToList();

        return new PlanCacheEvidenceResult(evidence, accumulated.PlansInspected, UnavailableReason: null);
    }

    /// <summary>
    /// Roadmap Phase D: the plan cache's own XML already tells us, for real, whether a column
    /// converts and whether the engine could still bound it with GetRangeThroughConvert - no
    /// static predicate typing needed at all, since this is live evidence rather than a guess.
    /// Most such conversions already surface as a module-derived static finding this same table/
    /// column would have produced anyway, so this only promotes the ones that DON'T - the
    /// dominant real-world case being ad-hoc, parameterized application-side SQL that was never
    /// a stored procedure body at all, and so was otherwise invisible to this tool entirely.
    /// </summary>
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
                // Same degrade-gracefully contract as ReadObservedConversionsAsync: on the last
                // attempt (a permission denial, or every retry hitting the same transient
                // instance-wide condition), yield "no workload findings" rather than failing the
                // whole scan-db run - the caller's UnavailableReason-bearing evidence result
                // (from ReadObservedConversionsAsync, run alongside this) already carries that
                // story. The `when` guard this project's own StatementVariantParityTests-style
                // discipline would flag was the actual bug here on the first pass: it let the
                // final attempt's exception propagate uncaught instead of degrading.
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
        // Keyed by (table, column) while accumulating - a tuple key is convenient here but
        // cannot be System.Text.Json-serialized directly (no built-in converter for a
        // ValueTuple dictionary key), so callers flatten to a plain list before returning.
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
        // GetRangeThroughConvert applies to the whole plan, not to one specific conversion node
        // within it - matching how TypeMatrixGenerator's own oracle probe reads this same signal.
        var hasRangeSeek = planXml.Contains("GetRangeThroughConvert", StringComparison.Ordinal);

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
            var existing = byColumn.GetValueOrDefault(key);
            byColumn[key] = new ColumnAccumulation(
                existing.ExecutionCount + executionCount, existing.HasRangeSeek || hasRangeSeek);
        }
    }

    private readonly record struct ColumnAccumulation(long ExecutionCount, bool HasRangeSeek);
}

/// <summary>Roadmap Phase D verdict for a workload-observed finding - only the two outcomes a real plan's XML can actually confirm (RangeSeek if GetRangeThroughConvert appears anywhere in the plan, ScanForced otherwise); SeekPreserved/Unknown/OperandClash never surface here because a column that didn't convert leaves no CONVERT_IMPLICIT for ConvertImplicitDetector to find in the first place.</summary>
public enum WorkloadVerdict
{
    ScanForced,
    RangeSeek,
}

/// <summary>
/// A conversion the live plan cache confirms is actually happening right now, for a (table,
/// column) pair no module-derived static finding already covers - overwhelmingly, ad-hoc
/// parameterized application-side SQL that was never a stored procedure body at all, and so was
/// otherwise entirely invisible to this tool. Confirmed by construction (it comes from a real
/// executed plan's own XML), not a static guess needing separate oracle verification.
/// </summary>
public sealed record WorkloadFinding(string TableQualifiedName, string ColumnName, bool Indexed, WorkloadVerdict Verdict, long ExecutionCount);

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
