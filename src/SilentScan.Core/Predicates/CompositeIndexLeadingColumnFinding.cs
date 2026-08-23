using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record CompositeIndexLeadingColumnFinding(
    string TableQualifiedName,
    string? IndexName,
    IReadOnlyList<string> IndexKeyColumns,
    string ViolatingColumnName,
    int ViolatingColumnPosition,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

