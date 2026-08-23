namespace SilentScan.Core.Predicates;

public sealed record TryCastComputedColumnPredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    string DefinitionText,
    string DefinitionSourcePath,
    int DefinitionLine,
    string PredicateSourcePath,
    int PredicateLine,
    int PredicateColumn,
    FindingConfidence Confidence = FindingConfidence.High);
