
namespace SilentScan.Core.Predicates;

public sealed record TryCastComputedColumnPredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    string DefinitionText,
    SourceSpan DefinitionLocation,
    SourceSpan Location,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding;
