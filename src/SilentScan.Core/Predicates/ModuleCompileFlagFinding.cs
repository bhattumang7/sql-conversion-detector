using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum ModuleCompileFlagFindingKind
{
    RecompilesEveryCall,

    TableValuedFunctionReturnUsesDatabaseCollation,
}

public sealed record ModuleCompileFlagFinding(
    ModuleCompileFlagFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ModuleCompileFlagRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

