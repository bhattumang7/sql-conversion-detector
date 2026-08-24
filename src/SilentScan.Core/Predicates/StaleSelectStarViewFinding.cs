using System.Text.Json.Serialization;


namespace SilentScan.Core.Predicates;

public sealed record StaleSelectStarViewFinding(
    string ViewQualifiedName,
    string BaseTableQualifiedName,
    IReadOnlyList<string> ViewCompiledColumns,
    IReadOnlyList<string> BaseTableCurrentColumns,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

