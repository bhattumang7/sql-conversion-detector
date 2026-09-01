using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum GeneratedAlwaysColumnAssignmentKind
{
    ExplicitInsertValue,

    ExplicitUpdateValue,
}

public sealed record GeneratedAlwaysColumnAssignmentFinding(
    GeneratedAlwaysColumnAssignmentKind Kind,
    string TableQualifiedName,
    string ColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.GeneratedAlwaysColumnAssignmentRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
