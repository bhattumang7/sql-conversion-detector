using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum SecurityFindingKind
{
    HardCodedCredential,

    HardCodedIpAddress,

    WeakHashAlgorithm,

    WeakHashAlgorithmInSensitiveContext,

    UnprovableDynamicSqlText,
}

public sealed record SecurityFinding(
    SecurityFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

