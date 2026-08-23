using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record ColumnCollationDriftFinding(
    string TableQualifiedName,
    string ColumnName,
    string ColumnCollationName,
    string BaselineCollationName,
    bool IsTempObject,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

