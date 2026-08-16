namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Post-expansion join width".
/// Every surveyed tool counts tables in the written <c>FROM</c>/<c>JOIN</c> list and warns past a
/// threshold; that count is meaningless when half the sources are views - the number that
/// matters is the EXPANDED one, base tables after resolving every view/inline-TVF reference
/// transitively through <see cref="Lineage.ViewExpansionMap"/>. A query that *looks* like a
/// three-table join and expands to twenty is the finding a written-count-only tool can never see -
/// this is the number nobody else can compute, since it needs this tool's own lineage pass.
///
/// Ranked by <see cref="ExpandedCount"/> minus <see cref="WrittenCount"/> (the gap) - fires at a
/// gap ≥ 3, catching "looks small, is actually large" even when the absolute expanded count is
/// modest. <b>Deliberately does NOT claim a specific "past N the optimizer gives up exhaustive
/// join-order search" threshold</b> - the checklist's own text flags that number as folklore
/// requiring oracle confirmation (a `StatementOptmEarlyAbortReason` plan-XML sweep) before being
/// quoted as fact, and that sweep has not been run. The counting mechanism itself needs no such
/// confirmation (it's exact structural arithmetic over the already-verified lineage pass), so it
/// ships now; the absolute-threshold claim is a documented, deliberately deferred follow-up.
///
/// <see cref="PartiallyUnexpanded"/> is true when some reference in the FROM clause (a derived
/// table, an MSTVF/CLR TVF fence, a dynamic/unmodeled construct) could not be expanded further -
/// <see cref="ExpandedCount"/> is then a lower bound, never claimed as exhaustive, matching this
/// codebase's "never guess" discipline throughout. <see cref="InflatingSources"/> names which
/// written FROM-clause reference(s) actually contributed the expansion, so a reader can see WHERE
/// the width came from, not just that it exists.
///
/// Not verdict-bearing, no oracle needed for the shipped claim (the gap is a structural fact, not
/// a plan-shape one) - <see cref="FindingConfidence.High"/>, SARIF Warning (structural risk,
/// matching <see cref="ForcedSerialFinding"/>/<see cref="CatchAllPredicateFinding"/>'s own tier).
/// Deliberately scoped to <c>SELECT</c> statements in v1, matching <see cref="MultiReferencedCteFinding"/>'s
/// own scope decision - a known v1 limit, not a silently-missed case.
/// </summary>
public sealed record PostExpansionJoinWidthFinding(
    string ModuleQualifiedName,
    int WrittenCount,
    int ExpandedCount,
    IReadOnlyList<string> ExpandedBaseTables,
    IReadOnlyList<string> InflatingSources,
    bool PartiallyUnexpanded,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.High);
