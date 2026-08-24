using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record PostExpansionJoinWidthFinding(
    string ModuleQualifiedName,
    int WrittenCount,
    int ExpandedCount,
    IReadOnlyList<string> ExpandedBaseTables,
    IReadOnlyList<string> InflatingSources,
    bool PartiallyUnexpanded,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

