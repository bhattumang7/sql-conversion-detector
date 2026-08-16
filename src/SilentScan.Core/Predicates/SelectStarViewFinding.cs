namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "SELECT * inside a view or
/// inline TVF". A view/inline TVF whose own outermost SELECT is a bare or qualified <c>SELECT *</c>
/// AND itself nests ≥ 1 view/TVF layer deep (<see cref="Lineage.ViewExpansionOrigin.Depth"/>, the
/// same metric <see cref="NestedViewDepthFinding"/> already ships) - a materially different claim
/// than the plain, deliberately-skipped "SELECT * is bad style" rule: the frozen column list this
/// produces silently disagrees with the base table after any change, AND forces every consumer to
/// carry the full width whether or not it needs it, which is how a covering index stops covering.
///
/// <b>Stronger than a mere caching staleness claim - confirmed directly against the oracle, not
/// assumed.</b> A view's <c>SELECT *</c> column list is bound at CREATE/ALTER time and stays
/// frozen even through <c>sys.dm_exec_describe_first_result_set</c> (the same live, describe-only
/// ground truth this codebase's own live-parity gate otherwise trusts) and through a REAL
/// EXECUTION of the view - not merely a stale catalog cache CLAUDE.md's live-parity rule already
/// accounts for, but a genuinely different current answer until <c>sp_refreshview</c> runs. Only
/// fires when a real, different consuming query elsewhere in the corpus explicitly selects a
/// strict, named subset of the view's full column set (<see cref="ConsumerSelectedColumns"/> ⊊
/// <see cref="ViewFullColumns"/>) - a consumer that itself does <c>SELECT *</c> never narrows
/// anything by construction, and is never matched.
///
/// One finding per (candidate view, consuming query site) pair, not deduplicated per view - the
/// actionable unit is "this specific consumer defeats this specific covering-index story," which
/// is genuinely per-site, matching <see cref="PostExpansionJoinWidthFinding"/>'s own per-query-site
/// granularity rather than <see cref="NestedViewDepthFinding"/>'s per-object one.
///
/// Deliberately scoped to v1: only the view's own OUTERMOST query specification's <c>*</c> is
/// inspected (a <c>*</c> nested only inside an inner derived-table subquery does not itself
/// qualify the view), a top-level <c>UNION</c>ed view declines rather than guessing which branch's
/// star matters, and only columns in the consumer's own SELECT LIST count as "selected" - a
/// column referenced only in WHERE/JOIN/ORDER BY/GROUP BY still forces the same read at the base-
/// table layer, but is out of this rule's scope (a documented v1 limit, not silently missed).
///
/// Not verdict-bearing, no oracle needed for the shipped claim (a pure catalog/lineage/AST fact
/// about column-set drift risk, not a plan-shape claim) - <see cref="FindingConfidence.High"/>,
/// SARIF Warning (the same "structural/maintenance risk" tier <see cref="NestedViewDepthFinding"/>/
/// <see cref="ForcedSerialFinding"/>/<see cref="CatchAllPredicateFinding"/> use, not the purely-
/// informational Note tier <see cref="CascadingForeignKeyFinding"/> gets - this finding names a
/// specific, actionable consumer/view pair, not just a structural fact). The "covering index
/// defeated" framing is risk-color, not a proven-defeated claim - whether the optimizer actually
/// inlines and prunes the view down to the consumer's own selected columns before touching the
/// base table is a genuine plan-shape question this stream deliberately does not attempt to prove
/// or disprove (CLAUDE.md: "static verdicts never depend on the cardinality estimator").
///
/// Version-insensitive: CREATE/ALTER VIEW-time column-list binding is stable, ancient T-SQL
/// behavior, unaffected by compat level or CE mode.
/// </summary>
public sealed record SelectStarViewFinding(
    string ViewQualifiedName,
    string ViewSourcePath,
    int ViewLine,
    IReadOnlyList<string> ViewFullColumns,
    int ViewDepth,
    string ConsumerSourcePath,
    int ConsumerLine,
    IReadOnlyList<string> ConsumerSelectedColumns,
    FindingConfidence Confidence = FindingConfidence.High);
