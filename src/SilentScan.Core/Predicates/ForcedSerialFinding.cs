namespace SilentScan.Core.Predicates;

public enum ForcedSerialFindingKind
{
    /// <summary>A DECLARE'd table variable is the write target of an INSERT/UPDATE/DELETE/MERGE, or the INTO target of an OUTPUT clause - the engine forces the whole modifying statement's plan serial (DOP 1), confirmed as <c>NonParallelPlanReason="TableVariableTransactionsDoNotSupportParallelNestedTransaction"</c> in a real executed plan. A read-only reference to the same table variable is unaffected - a direction-style distinction, not a "table variables are always serial" one.</summary>
    TableVariableModification,

    /// <summary>A cursor declared FAST_FORWARD (or the equivalent bare FORWARD_ONLY READ_ONLY, lacking an explicit STATIC/KEYSET/DYNAMIC) forces the cursor's own defining query plan serial - confirmed as <c>NonParallelPlanReason="NoParallelFastForwardCursor"</c>. This is the OPPOSITE of the common "always use LOCAL FAST_FORWARD" advice for row-by-row fetch overhead - that advice is still correct for fetch cost, but it is specifically what defeats a parallel plan for the cursor's defining SELECT. STATIC/KEYSET/DYNAMIC cursors were oracle-checked and do NOT trigger this same mechanism.</summary>
    FastForwardCursor,

    /// <summary>One of a finite, oracle-confirmed list of intrinsic functions/globals (@@TRANCOUNT, OBJECT_ID, IDENT_CURRENT, ERROR_NUMBER, ERROR_MESSAGE, ERROR_LINE, ERROR_SEVERITY, ERROR_STATE, ERROR_PROCEDURE) referenced inside a query with a real FROM clause - confirmed as <c>NonParallelPlanReason="NonParallelizableIntrinsicFunction"</c>. Several commonly-cited "always serial" intrinsics (@@ROWCOUNT, @@IDENTITY, @@ERROR, SCOPE_IDENTITY(), NEWID()) were oracle-checked and do NOT trigger this - deliberately excluded rather than guessed into the list.</summary>
    NonParallelizableIntrinsic,
}

/// <summary>
/// docs/detection-checklist.md Tier 2 "Forced-serial construct inventory" - three independently
/// oracle-confirmed constructs that force SQL Server to disable parallelism (effective MAXDOP 1)
/// for the statement/query that contains them, each carrying its own real,
/// <c>NonParallelPlanReason</c>-attributed mechanism rather than a shared guess. A performance-cost
/// finding, not a correctness one: forced-serial execution never changes the result, only its
/// cost - <see cref="FindingConfidence.High"/> by default, but reported at SARIF Warning (not
/// Error), the same "structural risk, not provably-wrong-result" tier <c>CatchAllPredicateFinding</c>/
/// <c>SetOptionFinding</c> already use.
///
/// <b>Scope, deliberately narrow to what the Docker oracle actually confirmed</b> (never a guess):
/// DYNAMIC/KEYSET cursors, additional catalog-metadata intrinsics beyond the nine listed on <see
/// cref="ForcedSerialFindingKind.NonParallelizableIntrinsic"/> (e.g. OBJECTPROPERTY, COL_LENGTH),
/// and the checklist's own "serial-zone constructs as informational" bullet (TOP row goals,
/// recursive CTEs, global scalar aggregates - MSTVF references are already covered by the shipped
/// TVF-fence stream) are all explicitly out of v1 scope - real, plausible candidates for the same
/// family, but none independently oracle-confirmed to share this exact mechanism, so none are
/// included rather than guessed at.
///
/// A table-variable finding's forced-serial scope is the ONE containing statement, not the whole
/// batch/procedure - an unrelated statement later in the same batch that never touches the table
/// variable stayed fully parallel in direct testing, so the finding's own wording must never imply
/// a wider blast radius than that.
///
/// Version-insensitive: all three mechanisms are long-standing, documented optimizer restrictions,
/// unaffected by compat level or CE mode - table-variable deferred compilation (SQL Server 2019/
/// compat 150+) improves only cardinality estimates for table variables and does NOT restore
/// parallelism, confirmed directly on this engine at its own default (and latest) compat level.
/// </summary>
public sealed record ForcedSerialFinding(
    ForcedSerialFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.High);
