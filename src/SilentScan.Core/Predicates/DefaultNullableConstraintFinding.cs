using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record DefaultNullableConstraintFinding(
    string TableQualifiedName,
    string ColumnName,
    string DefaultDefinitionText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

