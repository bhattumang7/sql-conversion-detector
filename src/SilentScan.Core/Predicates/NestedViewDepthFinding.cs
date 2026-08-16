namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Nested-view depth report". A
/// view/inline TVF nested ≥ 2 view/TVF layers deep before reaching a base table - structural
/// depth, not a claim that a query through it is currently slow (it may still seek fine); the
/// finding is the maintenance/robustness risk that grows with depth: a change to a base table now
/// has to be traced through 2+ independent view layers before its blast radius is understood, and
/// each layer is a place a `SELECT *`/column-list mismatch or a silent type widening can hide.
///
/// Catalog/lineage-only, unconditional - reported once per view/inline TVF whose own definition
/// crosses the threshold, independent of whether any scanned query actually calls it (the same
/// "reported once per object" precedent <see cref="MaxTypedColumnFinding"/> already establishes).
/// Threshold N = 2 (real prevalence measured against the local RM_ test database: depth 0/1/2/3 =
/// 80/37/17/3 of 137 views touching another view at all - depth 1 is common and not itself
/// notable, depth ≥ 2 is a small, real, selective signal rather than an inventory of every
/// view-over-view).
///
/// <see cref="Chain"/> is top-down: the reported view first, then each further-nested view it
/// passes through, ending just before <see cref="BaseTables"/> - the order a reader debugging
/// "why is this view slow/wrong" actually wants, starting at what they're looking at.
/// <see cref="BaseTables"/> lists every DISTINCT base table this view transitively bottoms out at
/// (a view can fan out to many). A cyclic view dependency (already reported separately by the
/// live-parity/lineage pass) contributes no depth here rather than looping.
///
/// <see cref="FindingConfidence.High"/> (depth is exact, not inferred), SARIF Warning (structural
/// risk, matching <see cref="ForcedSerialFinding"/>/<see cref="CatchAllPredicateFinding"/>'s own
/// tier - a real cost, not a correctness claim). No oracle needed: depth is a pure catalog/AST
/// fact, not a plan-shape claim. Version-insensitive: pure DDL-dependency structure, unaffected by
/// compat level or CE mode.
/// </summary>
public sealed record NestedViewDepthFinding(
    string ViewQualifiedName,
    int Depth,
    IReadOnlyList<string> Chain,
    IReadOnlyList<string> BaseTables,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.High);
