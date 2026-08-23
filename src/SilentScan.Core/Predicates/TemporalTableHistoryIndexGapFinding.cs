using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record TemporalTableHistoryIndexGapFinding(
    string CurrentTableQualifiedName,
    string HistoryTableQualifiedName,
    string? CurrentIndexName,
    IReadOnlyList<string> KeyColumns,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

