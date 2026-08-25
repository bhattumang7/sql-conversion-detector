using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum ForcedSerialFindingKind
{
    TableVariableModification,

    FastForwardCursor,

    NonParallelizableIntrinsic,
}

public sealed record ForcedSerialFinding(
    ForcedSerialFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ForcedSerialRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

