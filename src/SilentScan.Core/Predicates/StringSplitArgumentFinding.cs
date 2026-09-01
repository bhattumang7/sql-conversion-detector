using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum StringSplitArgumentFindingKind
{
    SeparatorNotSingleCharacter,
}

public sealed record StringSplitArgumentFinding(
    StringSplitArgumentFindingKind Kind,
    string SeparatorText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.StringSplitArgumentRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
