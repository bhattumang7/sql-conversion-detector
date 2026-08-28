using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record SpExecuteSqlParameterMismatchFinding(
    string? CallerScopeQualifiedName,
    string ParameterName,
    string CallerExpressionDisplay,
    string CallerTypeDisplay,
    string DeclaredParameterTypeDisplay,
    WriteLossKind Kind,
    bool IsOutputWriteback,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.SpExecuteSqlParameterMismatchRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
