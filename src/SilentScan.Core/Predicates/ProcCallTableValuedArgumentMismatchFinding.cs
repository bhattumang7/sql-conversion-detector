using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record ProcCallTableValuedArgumentMismatchFinding(
    string? CallerScopeQualifiedName,
    string CalleeQualifiedName,
    string FormalParameterName,
    string TableTypeQualifiedName,
    string ColumnName,
    string CallerExpressionDisplay,
    string CallerTypeDisplay,
    string ColumnTypeDisplay,
    WriteLossKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ProcCallTableValuedArgumentMismatchRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
