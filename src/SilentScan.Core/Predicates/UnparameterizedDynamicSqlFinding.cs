using System.Text.Json.Serialization;
using SilentScan.Core.Rules;


namespace SilentScan.Core.Predicates;

public enum UnparameterizedDynamicSqlFindingKind
{
    ConcatenatedValueInConstantSql,

    ExecStringConcatenatesParameterizableValue,
}

public sealed record UnparameterizedDynamicSqlFinding(
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    UnparameterizedDynamicSqlFindingKind Kind,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.UnparameterizedDynamicSqlRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

