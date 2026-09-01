using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record LegacyLobUtf8CollationFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    string CollationName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.LegacyLobUtf8CollationRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
