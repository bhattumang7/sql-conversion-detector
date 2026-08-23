using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record SecurityPredicateIndexFinding(
    string PolicyQualifiedName,
    string TableQualifiedName,
    string PredicateFunctionQualifiedName,
    IReadOnlyList<string> FilteredColumns,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

