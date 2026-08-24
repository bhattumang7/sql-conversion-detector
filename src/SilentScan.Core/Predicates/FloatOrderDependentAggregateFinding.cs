using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record FloatOrderDependentAggregateFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    string AggregateFunctionName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
