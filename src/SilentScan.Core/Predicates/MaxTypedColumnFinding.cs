using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum NonIndexableColumnFindingKind
{
    MaxLength,

    LegacyLargeObject,
}

public sealed record MaxTypedColumnFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    NonIndexableColumnFindingKind Kind = NonIndexableColumnFindingKind.MaxLength,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

