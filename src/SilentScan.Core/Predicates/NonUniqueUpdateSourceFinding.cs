using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record NonUniqueUpdateSourceFinding(
    string TargetTableQualifiedName,
    string SourceTableQualifiedName,
    IReadOnlyList<string> JoinColumnNames,
    IReadOnlyList<string> SetColumnNames,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

