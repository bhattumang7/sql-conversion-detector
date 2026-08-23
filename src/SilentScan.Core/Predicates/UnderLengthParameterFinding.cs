using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record UnderLengthParameterFinding(
    string TableQualifiedName,
    string ColumnName,
    int ColumnLength,
    int? OtherOperandLength,
    bool IsImplicitDefault,
    string Operator,
    bool ChangesRangeOrPatternShape,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

