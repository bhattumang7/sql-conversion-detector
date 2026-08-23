using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum IdentityRangeFindingKind
{
IdentitySeedOrIncrementAnomaly,

IdentityRangeNearExhaustion,
}

public sealed record IdentityRangeFinding(
    IdentityRangeFindingKind Kind,
    string TableQualifiedName,
    string ColumnName,
    string DetailText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

