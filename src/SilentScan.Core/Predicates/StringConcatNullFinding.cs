using System.Text.Json.Serialization;
using SilentScan.Core.TypeInference;


namespace SilentScan.Core.Predicates;

public sealed record StringConcatNullFinding(
    string TableQualifiedName,
    string ColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

