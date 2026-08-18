using System.Text.Json.Serialization;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A predicate compares a column that isn't really a column by the time the query sees it -
/// somewhere between here and the base table, a CAST/CONVERT or other expression (in this
/// statement's own derived table, or baked into an upstream view/TVF's SELECT list) replaced
/// it with a computed value. No comparison against a computed expression can use an index
/// seek, regardless of what type it lands on or what it's compared against - this is why it's
/// reported separately from <see cref="Rules.Verdict"/>, which is about type-precedence
/// mismatches between two otherwise-real operands, not this.
/// </summary>
/// <param name="ColumnName">The bare (unqualified) column name the predicate referenced.</param>
/// <param name="SourcePath">Where this finding's own predicate lives.</param>
/// <param name="Line">1-based line of the finding's own predicate.</param>
/// <param name="ColumnPosition">1-based column of the finding's own predicate.</param>
/// <param name="TransformationChain">Every layer (outermost first) that introduced a CAST/CONVERT or other expression between the predicate and the base column(s).</param>
/// <param name="UnderlyingBaseColumns">Every real base table column reachable underneath this expression-derived chain, and whether each is indexed.</param>
/// <param name="DynamicSqlCallSite">Set when this finding was found inside a reparsed dynamic SQL script - where the EXEC/sp_executesql call site lives.</param>
/// <param name="PredicateFragmentText">
/// Roadmap Phase E3: the whole enclosing predicate (e.g. <c>v.ComputedCol = 5</c>), re-rendered
/// to valid T-SQL text via <see cref="Rules.FragmentTextRenderer"/> at the moment this finding
/// was recorded - null when the column was found outside any comparison this pass tracks the
/// enclosing fragment for. Lets the corpus oracle actually probe this finding instead of only
/// trusting the lineage classifier that detected it.
/// </param>
/// <param name="ImmediateRelationQualifiedName">
/// The real, catalog-known view/TVF the predicate was actually written against (mirrors
/// <see cref="PredicateOperand.Column.ImmediateRelationQualifiedName"/>) - null when the column
/// came from an inline derived table/CTE in the same statement rather than a real, independently
/// queryable object, which a probe has no standalone way to reconstruct.
/// </param>
/// <param name="ImmediateRelationAlias">
/// The exact alias token the source predicate qualified the column with (e.g. the <c>v</c> in
/// <c>v.ComputedCol</c>), if any - a probe has to expose the SAME alias for
/// <paramref name="PredicateFragmentText"/>'s own qualified column reference to resolve.
/// </param>
/// <param name="Confidence">
/// How much this finding's own claim can be trusted - see <see cref="FindingConfidence"/>.
/// Defaults to <see cref="FindingConfidence.High"/>, same as every statically-derived finding.
/// </param>
public sealed record ExpressionDerivedFinding(
    string ColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int ColumnPosition,
    IReadOnlyList<TransformationSite> TransformationChain,
    IReadOnlyList<UnderlyingBaseColumn> UnderlyingBaseColumns,
    SourceSpan? DynamicSqlCallSite = null,
    string? PredicateFragmentText = null,
    string? ImmediateRelationQualifiedName = null,
    string? ImmediateRelationAlias = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<ExpressionDerivedFinding>
{
    public SourceSpan Location => new(SourcePath, Line, ColumnPosition);
    int IRelocatableFinding<ExpressionDerivedFinding>.PositionColumn => ColumnPosition;

    ExpressionDerivedFinding IRelocatableFinding<ExpressionDerivedFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}

/// <summary>A real base table column found underneath an expression-derived provenance chain, and whether it's indexed (which is what makes the finding worth fixing).</summary>
public sealed record UnderlyingBaseColumn(string TableQualifiedName, string ColumnName, bool Indexed);
