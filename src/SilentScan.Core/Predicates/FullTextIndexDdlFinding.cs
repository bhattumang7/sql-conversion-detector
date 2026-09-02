using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum FullTextIndexDdlFindingKind
{
    UnsupportedColumnType,

    InvalidLanguageId,

    NonDeterministicComputedColumn,

    TooManyIndexedColumns,
}

public sealed record FullTextIndexDdlFinding(
    FullTextIndexDdlFindingKind Kind,
    string TableQualifiedName,
    string? ColumnName,
    string Detail,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.FullTextIndexDdlRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
