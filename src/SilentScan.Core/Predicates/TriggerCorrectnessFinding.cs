namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §C "Trigger correctness" -
/// see <see cref="TriggerCorrectnessScanner"/> for the per-kind precision story and oracle
/// evidence. <see cref="StatementShapeFindingKind.MissingSetNocountOn"/> already covers the
/// sweep's "trigger without SET NOCOUNT ON" bullet (it already visits <c>CREATE/ALTER TRIGGER</c>
/// identically to a procedure) and is not duplicated here.
/// </summary>
public enum TriggerCorrectnessFindingKind
{
    /// <summary>
    /// A local variable assigned from <c>inserted</c>/<c>deleted</c> with no row-limiting
    /// mechanism at all: <c>SELECT @v = col FROM inserted</c> (a <see
    /// cref="Microsoft.SqlServer.TransactSql.ScriptDom.SelectSetVariable"/> element whose FROM
    /// clause is exactly the bare <c>inserted</c>/<c>deleted</c> pseudo-table, no <c>WHERE</c>, no
    /// <c>TOP</c>), or the structurally identical scalar-subquery form (<c>SET @v = (SELECT col
    /// FROM inserted)</c>/a <c>DECLARE @v ... = (SELECT col FROM inserted)</c> initializer) - never
    /// when the assigned expression is itself an aggregate (<c>COUNT</c>/<c>SUM</c>/<c>MAX</c>/...
    /// over the whole rowset IS a single, well-defined value regardless of row count, a materially
    /// different, unflagged shape). <c>inserted</c>/<c>deleted</c> can hold any number of rows for
    /// a single trigger invocation (a multi-row <c>INSERT</c>/<c>UPDATE</c>/<c>MERGE</c> against
    /// the trigger's own base table fires the trigger exactly ONCE with every affected row present
    /// in the pseudo-table, not once per row) - the engine raises no error for this shape, it
    /// silently binds <c>@v</c> to some single, unspecified row's value and discards the rest.
    /// Oracle-confirmed directly (Docker instance, disposable scratch database, dropped
    /// immediately after): a 3-row <c>UPDATE</c> against a seeded 3-row table fired an
    /// <c>AFTER UPDATE</c> trigger containing exactly this shape exactly once, and the variable
    /// ended up bound to the FIRST-inserted row's value (1100, i.e. row Id=1's new Amount) with
    /// the other two rows' values (1200, 1300) silently discarded - "which row wins" is not
    /// engine-guaranteed to be first/last/any particular one, only that it is a single arbitrary
    /// survivor. Correct for single-row DML, silently wrong for every multi-row invocation - no
    /// error raised, the same "silent data loss" family as the shipped write-loss stream, and the
    /// highest-severity kind in this whole sweep. <see cref="FindingConfidence.High"/>: the
    /// "single unspecified row wins" fact is a mechanical, oracle-confirmed engine behavior, not a
    /// magnitude estimate - whether a GIVEN trigger's caller ever actually issues multi-row DML
    /// against it is real-world usage this pass cannot see, which is exactly why this is reported
    /// as a real structural risk rather than a certainty.
    /// </summary>
    MultiRowUnsafeSingleRowAssignment,

    /// <summary>
    /// The sharper, more directly actionable sub-kind of <see
    /// cref="MultiRowUnsafeSingleRowAssignment"/>: the SAME unsafe single-row assignment, where the
    /// assigned variable is then used, straight-line within the SAME trigger body (the next
    /// top-level statement(s) in the trigger's own statement list, after unwrapping a single outer
    /// <c>BEGIN...END</c> exactly like <see cref="ProcCallGraphBuilder"/>'s own scope-body
    /// unwrapping - never traced through an <c>IF</c>/<c>WHILE</c>/<c>TRY</c> branch this pass has
    /// no fold-state to resolve, matching this codebase's "never guess across control flow it
    /// can't cheaply prove" discipline elsewhere), as the SOLE top-level equality predicate
    /// (<c>WHERE Col = @v</c>/<c>WHERE @v = Col</c>, the entire <c>WHERE</c> clause, nothing
    /// AND/OR-combined with it) of a subsequent <c>UPDATE</c> or <c>DELETE</c> against a real base
    /// table. This shows the arbitrary single value actually driving a write that should have
    /// touched every affected row from the original multi-row DML, not just the row whose value
    /// happened to survive the assignment - the same probe that confirmed <see
    /// cref="MultiRowUnsafeSingleRowAssignment"/> also confirmed this exact chain end-to-end (the
    /// arbitrary Amount value from row Id=1 was the one written back through a keyed
    /// <c>UPDATE ... WHERE Id = @amt</c>-shaped statement in the same trigger body). <see
    /// cref="FindingConfidence.High"/> for the same reason as the general kind - the mechanism is
    /// mechanical and oracle-confirmed, only real-world call pattern (never a maybe this pass
    /// tracks) is unknown.
    /// </summary>
    MultiRowUnsafeKeyedDml,

    /// <summary>
    /// A trigger body with no <c>IF NOT EXISTS (SELECT * FROM inserted/deleted)</c>/
    /// <c>IF @@ROWCOUNT = 0 RETURN</c>-shaped early-out guard anywhere in the trigger's own top-
    /// level body. A well-documented convention (skip unnecessary work when the pseudo-tables
    /// happen to be empty - some DML forms and replication/CDC-driven writes can invoke a trigger
    /// with zero affected rows), not a proven correctness defect: an empty-<c>inserted</c>/
    /// <c>deleted</c> invocation still runs the trigger's own body harmlessly against zero rows in
    /// the overwhelming majority of real trigger bodies (a bare aggregate/JOIN against an empty
    /// pseudo-table just produces zero rows of further work). Deliberately reported as genuinely
    /// LOW confidence and worded as advisory, never as a defect - this pass has no way to tell
    /// whether the specific trigger body would actually behave incorrectly (rather than merely
    /// wastefully) against an empty invocation, and many real trigger bodies never need the guard
    /// at all. <see cref="FindingConfidence.Low"/>.
    /// </summary>
    NoEarlyOutForEmptyInvocation,

    /// <summary>
    /// A trigger whose own body contains a direct <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>/
    /// <c>MERGE</c> against the EXACT SAME base table the trigger itself fires on (direct
    /// self-recursion - an indirect cycle through a second trigger/procedure is a materially
    /// different, unanalyzed shape). T-SQL silently no-ops this specific recursion path unless the
    /// connected database's own <c>RECURSIVE_TRIGGERS</c> option is ON - oracle-confirmed directly
    /// (Docker instance, disposable scratch database): with the option OFF (the engine default),
    /// a trigger that re-inserts into its own table fired exactly once despite the self-insert;
    /// with the SAME trigger body and only <c>RECURSIVE_TRIGGERS</c> flipped ON, it recursed for
    /// real (5 firings against a guard capped at 5, confirming genuine re-entry, not a fluke).
    /// So this finding only fires when <see cref="Catalog.DatabaseCatalog.IsRecursiveTriggersEnabled"/>
    /// is live-confirmed <c>true</c> - live-mode only, and honest by construction: a file-mode scan
    /// or a live scan against a database with the option OFF (or never read) never claims a risk
    /// that is not actually live. <see cref="FindingConfidence.Medium"/>: the recursion mechanism
    /// itself is mechanical and oracle-confirmed once the gating option is true, but whether the
    /// recursive branch is ever actually reached at runtime (it may sit behind a condition this
    /// pass does not evaluate) is real data/control-flow this pass cannot fully resolve.
    /// </summary>
    DirectRecursiveTrigger,
}

public sealed record TriggerCorrectnessFinding(
    TriggerCorrectnessFindingKind Kind,
    string TriggerQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium);
