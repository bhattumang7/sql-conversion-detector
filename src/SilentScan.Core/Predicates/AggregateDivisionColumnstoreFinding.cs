using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "Aggregate argument
/// containing a division (or other error-prone scalar expression) that relies on short-circuit
/// elimination, on a table with a columnstore or batch-mode-eligible index" - a
/// <c>COUNT</c>/aggregate argument shaped like a <c>CASE</c>-guarded division (e.g. <c>SUM(CASE
/// WHEN Denom &lt;&gt; 0 THEN Num / Denom ELSE 0 END)</c>), where the CASE exists specifically so a
/// row with a zero (or otherwise error-prone) divisor takes the ELSE branch instead of erroring, on
/// a table backed by a columnstore index (whose scans and aggregates run under batch-mode,
/// vectorized execution rather than rowstore's per-row scalar evaluation).
///
/// <b>Shipped as a STRUCTURAL RISK FLAG ONLY, honestly downgraded from the checklist's original
/// framing</b> - matching the discipline <see cref="IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable"/>
/// and <see cref="IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization"/>
/// already established for a catalog-decidable structural precondition whose downstream harm this
/// pass cannot itself prove. Real, documented effort was spent trying to reproduce the underlying
/// claim directly against the standing Docker instance (a disposable scratch database, dropped
/// immediately after) before shipping: a 50,000-row table with a genuine, deliberately-seeded
/// zero-divisor subset, a real nonclustered columnstore index, and a live-confirmed
/// <c>ActualExecutionMode="Batch"</c> plan (<c>SET STATISTICS XML ON</c>) - across the CASE-guarded
/// form, a WHERE-filtered form (<c>SELECT SUM(Num/Denom) FROM t WHERE Denom &lt;&gt; 0</c>, the
/// "filter should logically run first" shape), a <c>GROUP BY</c>/hash-aggregate form, swapped
/// THEN/ELSE branch order, and <c>MAXDOP</c>-forced parallelism - every single variant returned the
/// correct, error-free result on this environment's engine build (SQL Server 2022, RTM-CU23). No
/// live divide-by-zero error was ever produced. This does NOT disprove the underlying mechanism -
/// the practitioner reports this checklist item is drawn from describe a real, historically-observed
/// class of bug in batch-mode CASE/expression evaluation (predominantly reported against earlier
/// columnstore-batch-mode engine generations, SQL Server 2016-2019 era) - but it means this tool
/// cannot claim to have proven the failure live on the engine build it actually runs against, unlike
/// every oracle-confirmed stream in this codebase. <see cref="FindingConfidence.Low"/> reflects
/// exactly that: a real, catalog-decidable structural co-occurrence (CASE-guarded, non-constant-
/// divisor division inside an aggregate argument, on a table carrying a columnstore index) reported
/// as a documented historical risk pattern worth a second look, never as a proven-wrong-result or
/// even a proven-current-engine-behavior claim - SARIF Note, the same severity tier <see
/// cref="ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer"/> uses for its own purely
/// informational, unverified-live claim.
///
/// <b>"batch-mode-eligible" scope-down</b>: SQL Server 2019+ (compat level 150+) can also run batch
/// mode over an ordinary ROWSTORE table with no columnstore index at all ("Batch Mode on Rowstore"),
/// triggered by the optimizer's own cost/cardinality estimate for that specific query - real, but
/// NOT schema-decidable (it depends on estimated row counts and the chosen plan, workload data this
/// static pass cannot see), so per CLAUDE.md's precision-first policy this finding only fires on the
/// definitively provable case: the table carries an actual columnstore index
/// (<see cref="Catalog.CatalogIndex.IsColumnstore"/>, clustered or nonclustered) - the same
/// structural-fact-only scope-down <see cref="IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable"/>
/// already uses for an analogous batch-mode-adjacent claim.
///
/// <b>AST+catalog-decidable</b>: an aggregate function call (<c>SUM</c>/<c>AVG</c>/<c>COUNT</c>/
/// <c>COUNT_BIG</c>/<c>MIN</c>/<c>MAX</c>) whose own argument tree contains a <c>CASE</c> expression
/// (simple or searched) with a division (<c>/</c>) inside one of its <c>THEN</c>/<c>ELSE</c> result
/// expressions, where that division's own right-hand operand (the divisor) is NOT a literal constant
/// (a literal divisor, e.g. <c>Amount / 100</c>, can never be zero and is not error-prone regardless
/// of execution mode, so is deliberately excluded rather than over-flagged) - cross-checked against
/// whether ANY table the containing query's own FROM clause resolves through a direct base-table
/// alias (the same "known v1 scope limit" restraint <see cref="FloatEqualityPredicateScanner"/>
/// already established) carries a columnstore index.
/// </summary>
public sealed record AggregateDivisionColumnstoreFinding(
    string AggregateFunctionName,
    string TableQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

