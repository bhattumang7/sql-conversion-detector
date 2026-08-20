using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A reference that puts an optimization fence into a query plan: a multi-statement or CLR
/// table-valued function, or an <c>INSERT ... EXEC</c>. The body is opaque to the optimizer, the
/// result is materialized without statistics, and the reference carries a fixed cardinality
/// guess (1 row under the legacy CE, 100 under the 2014+ CE) that propagates outward into join
/// order, join types and memory grants.
/// <para>
/// This detection exists only because the catalog knows what the text cannot say:
/// <c>FROM dbo.fn(@x)</c> is byte-for-byte identical for a harmless inline TVF and a fencing
/// multi-statement one, and only <c>sys.objects.type</c> tells them apart. An inline TVF never
/// produces a finding here; a name whose kind this scan could not establish never produces one
/// either, rather than being assumed either way.
/// </para>
/// <para>
/// This stream deliberately does NOT claim the function "should have been a CTE or a view."
/// Whether a rewrite is possible depends on what the body does, which is a judgment call this
/// tool does not make (docs/detection-checklist.md keeps that case out of scope). What is
/// reported is the fence itself, which is a fact about the plan.
/// </para>
/// </summary>
/// <param name="Kind">How the fence is being reached - also the ranking order.</param>
/// <param name="FunctionQualifiedName">
/// The function whose body is the fence. For <see cref="TvfFenceFindingKind.NestedUnderViewOrTvf"/>
/// this is the multi-statement TVF found underneath, NOT the view/iTVF actually named at the call
/// site (that is <paramref name="ReferencedObjectQualifiedName"/>). Null for
/// <see cref="TvfFenceFindingKind.InsertExec"/>, which has no function at all.
/// </param>
/// <param name="ReferencedObjectQualifiedName">
/// What the call site literally names. Equal to <paramref name="FunctionQualifiedName"/> for a
/// direct reference; for a nested finding it is the innocent-looking view or inline TVF the
/// author actually wrote, which is what makes that case invisible without the lineage pass. For
/// <see cref="TvfFenceFindingKind.InsertExec"/> it is the procedure being executed, when that
/// resolved to a name at all.
/// </param>
/// <param name="FunctionKind">
/// The catalog's own classification of <paramref name="FunctionQualifiedName"/> - never
/// <see cref="TableValuedFunctionKind.Inline"/>, since an inline TVF is not a fence and does not
/// produce a finding. Null exactly when <paramref name="FunctionQualifiedName"/> is.
/// </param>
/// <param name="SourcePath">Where this finding's own reference lives.</param>
/// <param name="Line">1-based line of the reference.</param>
/// <param name="Column">1-based column of the reference.</param>
/// <param name="Depth">
/// How many view/TVF layers sit between the call site and the fencing function - CLAUDE.md's
/// depth field, the same meaning it carries on <see cref="Lineage.ColumnProvenance.BaseColumn"/>.
/// 0 for a direct reference; N for a fence inherited through N layers.
/// </param>
/// <param name="OriginSourcePath">
/// Where the layer that INTRODUCED the fence is defined - for a nested finding, the file
/// defining the view/TVF whose body names the multi-statement function, which is where a fix has
/// to happen. Null for a direct reference, where the origin is the finding's own location and
/// repeating it would say nothing.
/// </param>
/// <param name="OriginLine">1-based line within <paramref name="OriginSourcePath"/>; 0 when that is null.</param>
/// <param name="CorrelatedOuterColumns">
/// For <see cref="TvfFenceFindingKind.CorrelatedApply"/>, the outer-relation columns the
/// arguments reference - the evidence that this reference is correlated and therefore outside
/// interleaved execution's reach. Empty for every other kind.
/// </param>
/// <param name="ReferenceFragmentText">
/// The reference's own source text, re-rendered to valid T-SQL (<see cref="Common.FragmentTextRenderer"/>),
/// so the oracle can build a probe from the finding rather than trusting the classifier that
/// produced it - the same role this field plays on <see cref="SargabilityFinding"/>.
/// </param>
/// <param name="DynamicSqlCallSite">Set when the reference was found inside a reparsed dynamic SQL script - where the EXEC/sp_executesql call site lives, distinct from <paramref name="SourcePath"/>/<paramref name="Line"/>.</param>
/// <param name="Confidence">How much this finding's own claim can be trusted - see <see cref="FindingConfidence"/>.</param>
public sealed record TvfFenceFinding(
    TvfFenceFindingKind Kind,
    string? FunctionQualifiedName,
    string? ReferencedObjectQualifiedName,
    TableValuedFunctionKind? FunctionKind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    int Depth = 0,
    string? OriginSourcePath = null,
    int OriginLine = 0,
    IReadOnlyList<string>? CorrelatedOuterColumns = null,
    string? ReferenceFragmentText = null,
    SourceSpan? DynamicSqlCallSite = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<TvfFenceFinding>
{
    public SourceSpan Location => new(SourcePath, Line, Column);
    int IRelocatableFinding<TvfFenceFinding>.PositionColumn => Column;

    TvfFenceFinding IRelocatableFinding<TvfFenceFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}
