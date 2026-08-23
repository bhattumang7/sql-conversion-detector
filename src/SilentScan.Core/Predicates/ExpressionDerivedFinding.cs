using System.Text.Json.Serialization;
using SilentScan.Core.Lineage;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

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

public sealed record UnderlyingBaseColumn(string TableQualifiedName, string ColumnName, bool Indexed);
