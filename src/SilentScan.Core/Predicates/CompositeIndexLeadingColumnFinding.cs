using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Hint and index-shape catalog checks": "Composite index
/// leading-column violation" - a real composite index exists on the table (<see
/// cref="IndexKeyColumns"/>, ordered), the query genuinely constrains one of its NON-leading key
/// columns (<see cref="ViolatingColumnName"/> at <see cref="ViolatingColumnPosition"/>, position
/// &gt;= 1) via a real AND-reachable comparison, but the index's own leading key column (position
/// 0) is not referenced ANYWHERE in the statement at all - not even inside an OR branch or a
/// weaker comparison this scanner otherwise declines to treat as "constraining". A composite
/// index is a single ordered B-tree keyed first by its leading column; without a bound on that
/// leading column the engine cannot descend the tree to any useful starting point for this
/// predicate, so this specific index structurally cannot seek this specific query - the same
/// "suffix of a b-tree key can't be searched" fact one surveyed tool approximates with a regex,
/// made precise here against real index key ordering (docs/detection-reference.md Appendix 7).
///
/// Deliberately scoped as a per-index structural fact, never an index recommendation ("this query
/// cannot seek THIS index" - the checklist's own explicit scope note) - it says nothing about
/// whether the query is slow overall. The precision guard that keeps this from being noise on an
/// ordinary multi-index table: only fires when NO OTHER real, usable index on the table leads
/// with the SAME violating column, i.e. there is no alternative index this predicate could seek
/// through either - a table with a second index that DOES cover this predicate isn't "cannot
/// seek", it just doesn't seek THIS particular index, which is not a defect. Catalog-only
/// existence/shape facts once the AST has resolved which columns are genuinely constrained -
/// provable directly from <see cref="Catalog.CatalogIndex.KeyColumns"/>'s own ordering, so no
/// plan-XML oracle applies (the b-tree-prefix mechanism is architectural, not a cardinality-
/// dependent optimizer choice); <see cref="FindingConfidence.High"/> by construction.
///
/// Known v1 scope limits, stated honestly: only equality/range comparisons reachable without
/// crossing an OR count as "constraining" a column (mirrors <see
/// cref="PartialCompositeForeignKeyJoinScanner"/>'s own <c>FlattenAnd</c> discipline - a column
/// only bound inside an OR branch doesn't guarantee the leading column is ever supplied); only a
/// base-table-only, depth-0 predicate site is inspected (no CTE/view/temp-table scoping, the same
/// limit <see cref="CatchAllPredicateScanner"/> and <see cref="PartialCompositeForeignKeyJoinScanner"/>
/// already document); <c>SELECT</c>/<c>UPDATE</c>/<c>DELETE</c> only, <c>MERGE</c>'s own
/// <c>USING</c>/<c>ON</c> shape is out of v1 scope for the identical reason those two scanners
/// already gave theirs.
/// </summary>
public sealed record CompositeIndexLeadingColumnFinding(
    string TableQualifiedName,
    string? IndexName,
    IReadOnlyList<string> IndexKeyColumns,
    string ViolatingColumnName,
    int ViolatingColumnPosition,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

