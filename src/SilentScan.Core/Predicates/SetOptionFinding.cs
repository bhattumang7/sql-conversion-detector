using System.Text.Json.Serialization;
using SilentScan.Core.Rules;


namespace SilentScan.Core.Predicates;

public enum SetOptionFindingKind
{
    QuotedIdentifierOffBlocksIndexedFeature,

    NumericRoundabortOnBlocksIndexedFeature,

    AnsiNullsOffBlocksIndexedFeature,

    AnsiWarningsOffBlocksIndexedFeature,

    ConcatNullYieldsNullOffBlocksIndexedFeature,

    AnsiPaddingOffBlocksIndexedFeature,
}

public sealed record SetOptionFinding(
    SetOptionFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string? TouchedObjectQualifiedName = null,
    string? TouchedIndexName = null,
    bool TouchedIsIndexedView = false,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.SetOptionRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

