using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record NestedViewDepthFinding(
    string ViewQualifiedName,
    int Depth,
    IReadOnlyList<string> Chain,
    IReadOnlyList<string> BaseTables,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

