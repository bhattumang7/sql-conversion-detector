namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md practitioner-sweep item "Row-Level Security predicate function with
/// no supporting index on its own filtered columns" - an enabled RLS FILTER predicate is silently
/// applied to EVERY <c>SELECT</c>/<c>UPDATE</c>/<c>DELETE</c> against the secured table (not just
/// queries whose own <c>WHERE</c> clause happens to filter the same way), so a predicate function
/// bound to column(s) with no supporting index forces the engine to touch every row of the table on
/// every single access to it, evaluating the predicate as a residual, per-row filter instead of
/// seeking. Catalog+text decidable: <see cref="Catalog.CatalogSecurityPredicate.TargetTableQualifiedName"/>
/// names the secured table; <see cref="Catalog.CatalogSecurityPredicate.PredicateDefinitionText"/>
/// (reparsed - see that field's own doc comment for why there is no dedicated function-id column to
/// join through instead) names the predicate function and its own bound (secured-table) columns;
/// checking those columns against the table's actual indexes is the same shape <see
/// cref="IndexDesignScanner"/>'s own <see cref="IndexDesignFindingKind.UnindexedForeignKey"/> check
/// already uses.
///
/// <b>Oracle-confirmed directly against the standing Docker instance</b> (disposable scratch
/// database, dropped immediately after): a real <c>CREATE SECURITY POLICY ... ADD FILTER PREDICATE
/// Security.fn_TenantPredicate(TenantId) ON dbo.T WITH (STATE = ON)</c>, against a 50,000-row table.
/// With no index on <c>TenantId</c>, <c>SET STATISTICS XML ON</c> for a plain <c>SELECT COUNT(*)
/// FROM dbo.T</c> (<c>StmtSimple.SecurityPolicyApplied="true"</c>) showed a <c>Clustered Index
/// Scan</c> carrying the inlined predicate function's own logic as a residual
/// <c>&lt;Predicate&gt;</c> (<c>TenantId=CONVERT(int,session_context(N'TenantId'),0)</c>) evaluated
/// against every row (<c>EstimatedRowsRead</c> = the full table). With an index on <c>TenantId</c>
/// added and the identical query re-run, the plan switched to a genuine <c>Index Seek</c> with a
/// <c>SeekPredicate</c> on that column and no residual filter at all - the exact seek-vs-scan
/// contrast this codebase's other predicate-vs-index streams already document.
///
/// <b>The checklist's own "forces single-threaded execution" half is DROPPED here as an honest
/// scope correction</b> after a real, documented attempt to reproduce it live failed. A trivial-plan
/// probe (a bare <c>SELECT COUNT(*)</c>, <c>StatementOptmLevel="TRIVIAL"</c>) is always serial
/// regardless of RLS - that alone is not evidence of anything RLS-specific. Forcing a genuine
/// cost-based, non-trivial plan (<c>StatementOptmLevel="FULL"</c>, a <c>GROUP BY</c>/hash-aggregate
/// query, <c>cost threshold for parallelism</c> temporarily lowered to 0 so the optimizer would
/// actually consider a parallel plan) showed the RLS-secured query compile to <c>DegreeOfParallelism
/// = 12</c> - genuinely parallel, and identical to the same query re-run with the security policy
/// disabled (<c>SecurityPolicyApplied="false"</c>, also DOP 12). On this engine build (SQL Server
/// 2022, RTM-CU23), a standard inlineable FILTER predicate (an inline TVF, the pattern Microsoft's
/// own RLS documentation recommends and the only pattern this finding targets) does NOT force serial
/// execution. This may be a real historical restriction from an earlier engine generation, or may
/// apply only to a non-inlineable/BLOCK predicate shape this pass never targets - either way, this
/// tool does not claim it, since it could not confirm it on the engine it actually runs against.
///
/// <see cref="FindingConfidence.Medium"/>, matching the discipline <see
/// cref="IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable"/>/<see
/// cref="IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization"/> already use for
/// an exact, oracle-confirmed structural precondition (no supporting index on the predicate's own
/// bound columns → the engine cannot seek) whose actual real-world cost is still workload-dependent
/// (table size, access frequency) - a structural risk flag, not a proven-magnitude claim.
///
/// <b>Scope</b>: only an ENABLED (<see cref="Catalog.CatalogSecurityPredicate.IsPolicyEnabled"/>)
/// FILTER predicate (<see cref="Catalog.CatalogSecurityPredicate.IsFilterPredicate"/>) is inspected -
/// a BLOCK predicate does not filter the table's own read path the way a FILTER predicate does (see
/// <see cref="Catalog.CatalogSecurityPredicate"/>'s own doc comment), and a disabled policy is
/// provably inert. Only a predicate function invoked with at least one BARE COLUMN REFERENCE
/// argument is inspected - a predicate function called with a literal, expression, or no arguments
/// at all binds to nothing this pass can resolve against the table's own columns, and is left
/// unanalyzed entirely rather than guessed at. Fires when NONE of the predicate's own bound columns
/// individually leads an active (non-disabled, unfiltered, non-columnstore) index on the secured
/// table - deliberately column-by-column, not composite-leading-prefix the way <see
/// cref="IndexDesignFindingKind.UnindexedForeignKey"/> checks a single constraint's own column SET:
/// this pass cannot see the predicate function's own body, so it cannot tell whether multiple bound
/// columns are combined with AND (a composite leading-prefix index would suffice) or OR (each column
/// needs its own index) - requiring every bound column to individually lead some index is the
/// direction that never over-reports either way; a table where at least one bound column already
/// leads an index never fires, even though a different bound column might still lack one.
///
/// Live-mode only by construction: <see cref="Catalog.DatabaseCatalog.SecurityPredicates"/> is
/// populated only by <c>LiveCatalogReader</c> (RLS is a purely server-side binding with no in-module
/// DDL text a file-mode scan of application code would ever see), the same "always empty in file
/// mode" shape <see cref="CheckConstraintFinding"/> already documents for its own live-only catalog
/// input.
///
/// Version-insensitive: Row-Level Security itself shipped in SQL Server 2016 and its predicate-
/// application semantics are unchanged since; the forced-scan mechanism this finding reports does
/// not depend on compatibility level or CE mode. The dropped parallelism claim was checked
/// specifically against this environment's SQL Server 2022 (RTM-CU23) build and is not asserted for
/// any other version either.
/// </summary>
public sealed record SecurityPredicateIndexFinding(
    string PolicyQualifiedName,
    string TableQualifiedName,
    string PredicateFunctionQualifiedName,
    IReadOnlyList<string> FilteredColumns,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.Medium);
