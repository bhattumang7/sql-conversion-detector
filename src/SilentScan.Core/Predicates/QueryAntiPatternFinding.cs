namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §B "Query anti-patterns
/// still unbuilt" - the batch of items that survived precision scrutiny. One finding type, one
/// <c>Kind</c> discriminator, matching this codebase's established shared-plumbing shape (<see
/// cref="ControlFlowRiskFinding"/>/<see cref="IndexDesignFinding"/>).
/// </summary>
public enum QueryAntiPatternFindingKind
{
    /// <summary>A table variable (<c>DECLARE @t TABLE(...)</c> or a table-valued parameter) used
    /// as a query source in a <c>FROM</c>/<c>JOIN</c>, in a database connected at compatibility
    /// level BELOW 150 (SQL Server 2019's deferred-compilation fix for table variables never
    /// applies below that level, regardless of engine build). Oracle-confirmed directly (Docker
    /// instance, SQL Server 2022 engine, three compatibility levels): below level 150 the
    /// optimizer's cardinality estimate for a table variable is fixed at exactly 1 row no matter
    /// how many rows were actually loaded into it - confirmed with a 10,000-row population against
    /// a 50,000-row join partner, `EstimateRows="1"` in the real plan XML every time. Live-mode
    /// only - a file-mode scan has no live database to ask for its compatibility level (<see
    /// cref="Catalog.DatabaseCatalog.CompatibilityLevel"/>). <see cref="FindingConfidence.High"/>:
    /// this is a mechanical fact about the connected engine's own optimizer, not a magnitude
    /// estimate - it fires only when the level is provably below 150, never guessed when the level
    /// is unknown.</summary>
    TableVariableLowCompatEstimate,

    /// <summary>A table variable used as a query source (<c>FROM</c>/<c>JOIN</c>) inside the body
    /// of a <c>WHILE</c> loop that ALSO writes to the same table variable (INSERT/UPDATE/DELETE)
    /// somewhere in that same loop body - the variable grows/changes across iterations while being
    /// read on every iteration. Oracle-confirmed directly (Docker instance, compatibility level
    /// 160, deferred compilation active): the read statement's cardinality estimate is fixed at
    /// whatever the table variable's real row count was on the FIRST iteration that executed it,
    /// and never re-adjusts for later iterations even as the variable keeps growing - confirmed
    /// with a 5-iteration loop where each iteration added ~2,000 rows: every iteration's plan
    /// reported the SAME `EstimateRows="2000"` (the size after iteration 1), not the true,
    /// growing row count. Deferred compilation genuinely fixes the "populate once, read once"
    /// shape (see the sibling, now-closed checklist note on <see
    /// cref="TableVariableLowCompatEstimate"/>'s own doc comment) - this is the real, narrower
    /// case deferred compilation does NOT fix, and it is the one this project's original "does not
    /// fix it inside a multi-statement loop" assumption actually meant. Engine-version sensitive:
    /// this is specifically a SQL Server 2019+/compat-150+ nuance - below that level, <see
    /// cref="TableVariableLowCompatEstimate"/> already reports the even-worse always-1-row story
    /// for the same site, so this kind only fires when the connected compatibility level is
    /// unknown (file-mode) or provably 150+, to avoid a redundant, less-precise second finding on
    /// the same site a live scan already covers more sharply. <see cref="FindingConfidence.Medium"/>
    /// - a real, oracle-confirmed mechanism, but whether the loop's later iterations grow the
    /// table variable enough to matter is data-dependent and this pass cannot see it.</summary>
    TableVariableStaleEstimateInLoop,

    /// <summary>A <c>WHILE</c> loop body containing an UPDATE or DELETE whose <c>WHERE</c> clause
    /// is a single top-level equality comparison between a column and a local variable
    /// (<c>@v</c>), where that same variable is itself assigned somewhere else in the SAME loop
    /// body (a <c>SET</c>/<c>SELECT</c> assignment, or a cursor <c>FETCH ... INTO</c>) - the
    /// textbook row-by-row-processing (RBAR) shape: a loop that advances one tracked value per
    /// iteration and issues a single-row write keyed to exactly that value, where a single
    /// set-based statement over the whole matching set would do the same work without the
    /// per-iteration round-trip and (for a non-cursor loop) plan-compilation overhead. Only a
    /// single, top-level <c>column = @v</c>/<c>@v = column</c> equality is matched (AND-flattened,
    /// never through OR) - a composite or non-equality predicate is a materially different,
    /// unanalyzed shape, not a silently-missed case. <see cref="FindingConfidence.Medium"/>: a
    /// real, well-documented anti-pattern, but this pass cannot see how many rows the loop
    /// actually processes - a loop bounded to a genuinely tiny, known-small set is a real,
    /// sometimes-reasonable exception.</summary>
    RbarSingleRowLoopDml,

    /// <summary>A cursor declared without the <c>LOCAL</c> keyword - <c>DECLARE cur CURSOR FOR
    /// ...</c> or <c>DECLARE cur CURSOR GLOBAL FOR ...</c> (an explicit <c>GLOBAL</c> is the same
    /// risk, stated outright rather than left to the default) - defaults to <c>GLOBAL</c> per
    /// engine documentation, meaning the cursor stays alive and visible for the whole
    /// CONNECTION/batch scope, not just the declaring procedure - a resource that outlives its own
    /// declaring scope unless explicitly deallocated, and a naming collision risk if a caller and
    /// a called proc each declare a cursor with the same name (the inner declaration silently
    /// shadows/conflicts with the outer one's visibility, well-documented engine behavior, not
    /// this project's own inference). Distinct from the already-shipped <see
    /// cref="ForcedSerialFindingKind.FastForwardCursor"/>, which is about a different mechanism
    /// entirely (forced-serial query plans) and does not inspect <c>LOCAL</c>/<c>GLOBAL</c> at
    /// all. <see cref="FindingConfidence.Low"/>: `sp_configure 'default cursor
    /// option'` can flip the connection-level default to LOCAL, and many real procs rely on
    /// GLOBAL scope deliberately (rare but real) - purely informational, matching this codebase's
    /// own <see cref="ControlFlowRiskFindingKind.DirtyReadIsolationHint"/> precedent for a
    /// sometimes-deliberate default.</summary>
    GlobalCursorDeclaration,

    /// <summary>A local variable assigned <c>COUNT(*)</c> (or <c>COUNT(1)</c>/
    /// <c>COUNT_BIG(*)</c>) from a table (<c>SELECT @v = COUNT(*) FROM T [WHERE ...]</c>),
    /// immediately followed (ignoring intervening whitespace/comments - the very next statement in
    /// the same block) by an <c>IF</c>/<c>WHERE</c> test comparing that SAME variable only to zero
    /// (<c>@v &gt; 0</c>, <c>@v &gt;= 1</c>, <c>@v = 0</c>, <c>@v &lt;&gt; 0</c>), with no other use
    /// of the variable anywhere in between. Oracle-confirmed directly (Docker instance, 200,000-row
    /// seeded table, compat 160) that this SPECIFIC two-statement shape genuinely does a full
    /// `Stream Aggregate` over an `Index Seek` estimated at all 200,000 matching rows - a real,
    /// unavoidable full-set count. This is the deliberately narrow, oracle-verified-risky half of
    /// the "COUNT(*) as an existence test" idea: the SAME oracle run also confirmed the optimizer
    /// automatically rewrites the more commonly assumed shape - <c>IF (SELECT COUNT(*) FROM T
    /// WHERE ...) &gt; 0</c> with the aggregate written INLINE as a scalar subquery directly in the
    /// boolean comparison, never touching a variable - into a `Left Semi Join`/`Left Anti Semi
    /// Join` plan that short-circuits exactly like <c>EXISTS</c> (`EstimateRows="1"`, not
    /// 200,000), for every inline form tested (<c>&gt; 0</c>, <c>&gt;= 1</c>, and <c>WHERE (SELECT
    /// COUNT(*) ...) = 0</c> in an outer query). This project does NOT flag the inline scalar-
    /// subquery form at all - doing so would be a false claim this oracle run directly disproved,
    /// exactly the "commonly assumed" trap CLAUDE.md warns against publishing unverified. Only the
    /// variable-assignment form, which the SAME oracle run confirmed does NOT get the optimizer's
    /// rewrite, is reported. <see cref="FindingConfidence.High"/>: a mechanically confirmed,
    /// always-true-for-this-shape cost claim, not a maybe.</summary>
    CountStarVariableExistenceCheck,

    /// <summary>A <c>HAVING</c> clause condition whose own referenced columns are ALL either
    /// GROUP BY key columns or literals, and which does not reference any aggregate function
    /// result - a condition that could be moved to <c>WHERE</c> verbatim with an identical result,
    /// but as written filters rows AFTER the (potentially expensive) aggregation instead of
    /// before it. Deliberately conservative: a condition touching an aggregate result, or a
    /// column that is not a GROUP BY key and not a plain literal, is left unanalyzed rather than
    /// guessed at - this pass never reasons about whether a non-key column happens to be
    /// functionally dependent on the group. Only a single, top-level <c>QuerySpecification</c>'s
    /// own <c>HAVING</c>/<c>GROUP BY</c> pair is examined (AND-flattened, never through OR, same
    /// discipline as <see cref="NonUniqueUpdateSourceScanner"/>'s own join-condition flattening -
    /// a condition reachable only through an OR branch does not unconditionally qualify). A
    /// finding fires per AND-flattened branch, not once per whole HAVING clause: a mixed
    /// <c>HAVING Col = 'x' AND COUNT(*) &gt; 1</c> still fires for the <c>Col = 'x'</c> branch
    /// alone, since splitting a conjunctive HAVING at its own AND boundary and moving only the
    /// non-aggregate half to WHERE is itself a correct, independent rewrite regardless of what
    /// the sibling branch requires.
    /// <see cref="FindingConfidence.High"/>: correctness-preserving by construction - moving a
    /// GROUP-BY-key-only, non-aggregate condition to WHERE cannot change the result set, so this
    /// is a structural fact, not an estimate.</summary>
    NonAggregateHavingPredicate,

    /// <summary>A <c>UNION</c> (not <c>UNION ALL</c>) combining branches that are each a plain,
    /// single-base-table <c>SELECT</c> whose own <c>WHERE</c> clause is nothing but a single
    /// top-level equality comparison of the SAME column (on the SAME base table, resolved through
    /// the catalog) against a literal, where every branch's literal is provably distinct from
    /// every other branch's - since a row cannot equal two different literal values on the same
    /// column at once, the branches are provably mutually exclusive, so <c>UNION</c>'s own
    /// duplicate-elimination pass over the combined set can never actually remove a row, and
    /// <c>UNION ALL</c> would produce an identical result while skipping that pass. Deliberately
    /// the ONLY disjointness shape this project ships - CLAUDE.md's own explicit warning is not to
    /// ship a bare "you used UNION" shape match, and this is the one case genuinely provable
    /// without runtime information: a branch with a join, an OR, a non-equality comparison, or a
    /// non-literal comparand is left unanalyzed rather than guessed at. <see
    /// cref="FindingConfidence.Medium"/>: the claim itself is exact, but whether the extra
    /// duplicate-elimination pass actually costs anything measurable depends on the combined row
    /// count, which this pass cannot see.</summary>
    UnionOfProvablyDisjointBranches,

    /// <summary>A <c>SELECT DISTINCT</c> query with a <c>JOIN</c> whose second (joined-to)
    /// table's own join-equated columns are NOT backed by a unique, non-filtered, non-disabled
    /// catalog index - reuses <see cref="NonUniqueUpdateSourceScanner"/>'s own composite-
    /// uniqueness catalog check verbatim (a real unique index whose FULL key column set is a
    /// subset of the columns the <c>ON</c> clause equates to the joined table's own alias). A join
    /// that can genuinely multiply rows (no such uniqueness guarantee) combined with
    /// <c>DISTINCT</c> in the same query is a well-documented smell: <c>DISTINCT</c> may be
    /// silently papering over the join's own row duplication rather than expressing a deliberate
    /// business-logic requirement - if so, the join itself is doing more work than the query
    /// needs, and the fan-out is invisible to anyone reading the query without checking the
    /// catalog by hand. Only a join two hops from a <c>DISTINCT</c> top-level
    /// <c>QuerySpecification</c>'s own FROM tree is examined, and only equality join predicates
    /// are matched (AND-flattened) - the same v1 scope discipline <see
    /// cref="NonUniqueUpdateSourceScanner"/> already established for its own analogous claim.
    /// <see cref="FindingConfidence.Medium"/>: a real, catalog-provable fan-out risk, but
    /// <c>DISTINCT</c> can also be a genuine, deliberate requirement independent of the join
    /// (e.g. a report intentionally collapsing legitimate duplicates from the join's OTHER,
    /// unique-backed side) - this pass cannot tell those two intents apart from the query text
    /// alone.</summary>
    DistinctMaskingJoinFanout,
}

public sealed record QueryAntiPatternFinding(
    QueryAntiPatternFindingKind Kind,
    string SourcePath,
    int Line,
    int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium);
