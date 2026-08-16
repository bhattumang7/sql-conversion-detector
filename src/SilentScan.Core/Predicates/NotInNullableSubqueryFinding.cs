namespace SilentScan.Core.Predicates;

/// <summary>
/// <c>WHERE x NOT IN (SELECT y FROM t)</c> where <c>y</c> is a nullable column - a classic
/// three-valued-logic correctness trap (docs/detection-checklist.md Tier 2 "NOT IN over a
/// nullable subquery column"). <c>NOT IN</c> desugars to a chain of <c>&lt;&gt; ALL</c>
/// comparisons; the instant the subquery produces one <c>NULL</c> row, <c>x &lt;&gt; NULL</c> is
/// UNKNOWN, and ANDing UNKNOWN into the chain makes the whole <c>IN</c> UNKNOWN, which <c>NOT</c>
/// leaves UNKNOWN too - so <c>WHERE</c> silently drops every row, not just the ones that would
/// have matched the NULL. This is fundamental ANSI three-valued-logic semantics, not an optimizer
/// behavior: version-insensitive, unaffected by compat level or CE mode, confirmed directly
/// against the standing Docker oracle rather than assumed
/// (<c>NotInNullableSubqueryOracleTests</c>).
///
/// <b>Not a plan-shape finding - a correctness one.</b> No <c>Verdict</c>: the query returns the
/// WRONG RESULT SET the moment the underlying data contains a NULL in that column, independent of
/// any index or plan choice. <see cref="FindingConfidence.High"/> by default and reported at SARIF
/// <c>LevelError</c>, the same certainty tier as <c>AnsiPaddingMismatchFinding</c> and
/// <c>TemporalBoundaryPrecisionFinding</c> - not merely a conditional risk, but provably wrong for
/// this exact code today whenever the data hits the trap. Never downgraded by
/// <see cref="SubqueryColumnIndexed"/> - there is no seek/scan angle to this finding at all.
///
/// Deliberately narrow, base-table-only, Depth-0-only on the subquery side (matching
/// <c>CatchAllPredicateScanner</c>/<c>PartialCompositeForeignKeyJoinScanner</c>'s own scope): the
/// subquery must project a single bare column reference resolving to a base table - a projected
/// expression (<c>SELECT y + 1</c>, <c>SELECT ISNULL(y, 0)</c>), a view/CTE-derived column, or a
/// multi-column/<c>SELECT *</c>/set-operator (<c>UNION</c>/<c>EXCEPT</c>/<c>INTERSECT</c>)
/// subquery is left unanalyzed rather than guessed at - <c>ISNULL(y, 0)</c> in particular can
/// never itself be NULL, so guessing here would be a genuine false positive, not just an
/// over-cautious skip.
///
/// A subquery that already defends itself with a top-level (AND-chain) <c>WHERE y IS NOT NULL</c>
/// on the identical projected column never fires - that is exactly the fix real-world reports of
/// this bug use, and firing on already-fixed code would be a visible false positive on the single
/// most common remediation. A filter only reachable through an <c>OR</c> branch does not count -
/// it does not unconditionally exclude NULLs from every row the subquery could project.
/// </summary>
public sealed record NotInNullableSubqueryFinding(
    string? OuterColumnName,
    string SubqueryTableQualifiedName,
    string SubqueryColumnName,
    bool SubqueryColumnIndexed,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
