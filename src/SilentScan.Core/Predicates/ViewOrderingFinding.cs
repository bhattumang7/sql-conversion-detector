using System.Text.Json.Serialization;


namespace SilentScan.Core.Predicates;

public enum ViewOrderingFindingKind
{
    TopPercentOrderByNeverLimits,

    OrderByNotGuaranteedToConsumer,
}

public sealed record ViewOrderingFinding(
    ViewOrderingFindingKind Kind,
    string ObjectQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

