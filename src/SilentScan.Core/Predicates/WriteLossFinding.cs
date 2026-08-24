using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using System.Text.Json.Serialization;
using SilentScan.Core.TypeInference;


namespace SilentScan.Core.Predicates;

public sealed record WriteLossFinding(
    string? TableQualifiedName,
    string ColumnName,
    WriteLossKind Kind,
    SqlType TargetType,
    SqlType SourceType,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int ColumnPosition,
    SourceSpan? DynamicSqlCallSite = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<WriteLossFinding>, IFinding
{
    public SourceSpan Location => new(SourcePath, Line, ColumnPosition);
    int IRelocatableFinding<WriteLossFinding>.PositionColumn => ColumnPosition;

    WriteLossFinding IRelocatableFinding<WriteLossFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}
