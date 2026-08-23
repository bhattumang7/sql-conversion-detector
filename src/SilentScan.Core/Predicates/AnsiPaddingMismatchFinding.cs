using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record AnsiPaddingMismatchFinding(
    string TableQualifiedName,
    string ColumnName,
    string PatternLiteralText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

