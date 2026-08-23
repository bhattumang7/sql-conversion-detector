using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record CollationConflictFinding(
    string FirstTableQualifiedName,
    string FirstColumnName,
    string FirstCollationName,
    string SecondTableQualifiedName,
    string SecondColumnName,
    string SecondCollationName,
    string Operator,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int ColumnPosition,
    SourceSpan? DynamicSqlCallSite = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<CollationConflictFinding>
{
    public SourceSpan Location => new(SourcePath, Line, ColumnPosition);
    int IRelocatableFinding<CollationConflictFinding>.PositionColumn => ColumnPosition;

    CollationConflictFinding IRelocatableFinding<CollationConflictFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}
