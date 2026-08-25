using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum SessionDateSettingKind
{
    DateFormat,
    DateFirst,
}

public sealed record SessionDateSettingFinding(
    SessionDateSettingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Low) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.SessionDateSettingRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

