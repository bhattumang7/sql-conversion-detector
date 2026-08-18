using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": true cartesian join - a comma-join
/// or explicit <c>CROSS JOIN</c> where NO predicate anywhere in the statement (the WHERE clause,
/// or any other JOIN's own ON clause) connects the two sides at all. Deliberately distinct from
/// the shipped <see cref="PartialCompositeForeignKeyJoinFinding"/>: that fires when a join
/// predicate EXISTS but is incomplete; this fires when there is no predicate joining the pair at
/// all - pure relational algebra (a row-count-multiplying cartesian product), no engine-version
/// sensitivity, no plan-shape oracle needed.
///
/// <see cref="CartesianJoinKind.ExplicitCrossJoin"/> and <see cref="CartesianJoinKind.CommaJoin"/>
/// report separately (not identically) because they carry different intent signals: a written
/// <c>CROSS JOIN</c> is the author explicitly stating the cartesian product is deliberate
/// (<see cref="FindingConfidence.Medium"/> - still worth surfacing, since an accidentally-left
/// CROSS JOIN is a real, if less common, mistake, but lower confidence this is a bug than the
/// comma-join case below), while a legacy comma-join with no connecting predicate is the
/// classic "forgot the join condition" defect with no comparable self-documentation
/// (<see cref="FindingConfidence.High"/>, the record's own default).
///
/// **Known v1 scope limit, deliberate:** only fires when BOTH sides of the gap are themselves a
/// single plain <c>NamedTableReference</c> (no nested join/derived table/subquery on either side)
/// - resolving alias ownership through a nested join tree is real additional machinery out of
/// scope for this pass, matching this codebase's "decline rather than guess" discipline
/// elsewhere. Also declines whenever the statement's own WHERE clause or any ON clause contains
/// an UNQUALIFIED column reference anywhere - such a reference cannot be conservatively attributed
/// to one side without a catalog column lookup this pass doesn't perform, so the whole statement
/// is skipped rather than risking a false positive from mis-attributing which side it filters.
/// </summary>
public enum CartesianJoinKind
{
    CommaJoin,
    ExplicitCrossJoin,
}

public sealed record CartesianJoinFinding(
    CartesianJoinKind Kind,
    string FirstTableQualifiedName,
    string SecondTableQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

