using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;


namespace SilentScan.Core.Predicates;

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
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<TvfFenceFinding>, IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
    int IRelocatableFinding<TvfFenceFinding>.PositionColumn => Column;

    TvfFenceFinding IRelocatableFinding<TvfFenceFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}
