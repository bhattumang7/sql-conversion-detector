using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum SecurityFindingKind
{
    HardCodedCredential,

    HardCodedIpAddress,

    WeakHashAlgorithm,

    WeakHashAlgorithmInSensitiveContext,

    UnprovableDynamicSqlText,

    ExternalRestEndpointCall,
}

public sealed record SecurityFinding(
    SecurityFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.SecurityRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

