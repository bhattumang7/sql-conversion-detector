using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record ScalarUdfFinding(
    ScalarUdfFindingKind Kind,
    string FunctionQualifiedName,
    string ReferencedObjectQualifiedName,
    ScalarUdfKind UdfKind,
    ScalarUdfInlineability Inlineability,
    string? InlineabilityBlocker,
    bool? IsSchemaBound,
    bool ConstantArgumentsNotFolded,
    bool? ClrDataAccess,
    ScalarUdfContext Context,
    SchemaDependencyKind? SchemaDependencyKind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    int Depth = 0,
    string? OriginSourcePath = null,
    int OriginLine = 0,
    string? ReferenceFragmentText = null,
    SourceSpan? DynamicSqlCallSite = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<ScalarUdfFinding>, IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
    int IRelocatableFinding<ScalarUdfFinding>.PositionColumn => Column;

    ScalarUdfFinding IRelocatableFinding<ScalarUdfFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, Column = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}
