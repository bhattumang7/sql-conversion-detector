using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum IndexCoverageFindingKind
{
    KeyLookupProneIndex,
}

public sealed record IndexCoverageFinding(
    IndexCoverageFindingKind Kind,
    string TableQualifiedName,
    string? IndexName,
    IReadOnlyList<string> IndexKeyColumns,
    IReadOnlyList<string> IndexIncludedColumns,
    IReadOnlyList<string> UncoveredColumns,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

