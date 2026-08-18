using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>One column pair of a composite foreign key, in the same Parent/Referenced naming <see cref="Catalog.ForeignKeyRelationship"/> already uses (Parent = the table that DEFINES the FK columns, Referenced = the table those columns point at).</summary>
public sealed record ForeignKeyColumnPair(string ParentColumnName, string ReferencedColumnName);


/// <summary>
/// A JOIN's ON clause (or, for a legacy comma join, its WHERE-clause join condition) equates
/// SOME but not all of a real composite foreign key's column pairs (docs/detection-checklist.md
/// Tier 1 "Join predicate incomplete vs. the backing foreign key") - a genuine correctness AND
/// plan defect: the omitted column(s) let one parent row match more than one child row than the
/// declared relationship allows, silently multiplying rows through the join. Correctness finding,
/// not a verdict-bearing one - there is no seek/scan question here, the defect is the ROW COUNT
/// the join produces, not its access path (mirrors <see cref="TemporalBoundaryPrecisionFinding"/>
/// in kind, oracle-confirmed the same way: real seeded rows, a real row-count comparison, no plan
/// XML needed). Version-insensitive: row multiplication from a partial equality join is pure
/// relational algebra, unaffected by CE version, interleaved execution, or UDF inlining.
///
/// Catalog-only for FK discovery, but needs a genuine AST walk of the query text (unlike <see
/// cref="CrossTableTypeDriftFinding"/>) to see which columns the join predicate actually equates -
/// live-mode only, like every other <see cref="Catalog.DatabaseCatalog.ForeignKeys"/> consumer
/// (always empty in file mode). <see cref="MissingColumnPairs"/> is deliberately never empty AND
/// never equal to <see cref="AllColumnPairs"/> - both extremes (a full-composite join, or a join
/// that misses the FK entirely) are excluded by construction, the latter because "you didn't use
/// the FK" is a different, much lower-precision claim this stream does not make (see the
/// checklist's own scope note).
/// </summary>
public sealed record PartialCompositeForeignKeyJoinFinding(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ReferencedTableQualifiedName,
    IReadOnlyList<ForeignKeyColumnPair> AllColumnPairs,
    IReadOnlyList<ForeignKeyColumnPair> MatchedColumnPairs,
    IReadOnlyList<ForeignKeyColumnPair> MissingColumnPairs,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
