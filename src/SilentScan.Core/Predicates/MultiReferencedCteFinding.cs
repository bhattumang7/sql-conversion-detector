using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Multi-referenced CTE". SQL
/// Server does NOT materialize a plain (non-recursive) CTE once and reuse it across references -
/// each reference to the same CTE name re-runs the CTE's own defining query independently, so a
/// CTE referenced N times downstream costs N executions of its own query, not one. A real, well-
/// documented SQL Server behavior (unlike a temp table, which genuinely does materialize once) -
/// confirmed directly against the standing Docker oracle (real execution, <c>SET STATISTICS IO
/// ON</c>: a base table's logical-reads count doubled under a CTE referenced twice, matching two
/// independent scans of the same underlying data), not assumed from documentation or folklore -
/// the same "verify every claim against the oracle rather than trusting either source" discipline
/// this codebase applies everywhere, load-bearing here specifically because an earlier stream this
/// session (the FAST_FORWARD cursor finding) found a piece of "everyone knows this" SQL Server
/// folklore to be backwards once actually checked.
///
/// A self-reference inside a CTE's OWN defining query (the recursive-CTE anchor/recursive-member
/// shape - T-SQL has no separate <c>RECURSIVE</c> keyword; a CTE that references its own name
/// simply IS recursive) is never counted - that reference is the structurally mandated recursion
/// mechanism, not the optional re-invocation this finding targets. Only references reachable from
/// OUTSIDE the CTE's own query expression (the final query body, or another CTE's own body) count
/// toward <see cref="ReferenceCount"/>.
///
/// Deliberately scoped to <c>SELECT</c> statements in v1 - an <c>UPDATE</c>/<c>DELETE</c>/
/// <c>MERGE</c> statement's own WITH-clause CTEs are a real but comparatively rare shape, left
/// unanalyzed rather than guessed at (a known v1 scope limit).
///
/// Not verdict-bearing, no catalog/lineage dependency at all (syntax-only, single-statement) -
/// <see cref="FindingConfidence.High"/> (the reference count is an exact syntactic fact once
/// matched), SARIF Warning (a real, structural cost - the same "structural risk, not provably-
/// wrong-result" tier <see cref="ForcedSerialFinding"/>/<see cref="CatchAllPredicateFinding"/> use).
///
/// Version-insensitive: assumes no CTE auto-materialization, true on every current SQL Server
/// version/compat level - if a future engine version changes this, the premise would need
/// revisiting, stated explicitly per CLAUDE.md's engine-version-sensitivity requirement.
/// </summary>
public sealed record MultiReferencedCteFinding(
    string CteName,
    int ReferenceCount,
    IReadOnlyList<int> ReferenceLines,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

