using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record ForeignKeyColumnPair(string ParentColumnName, string ReferencedColumnName);

public sealed record PartialCompositeForeignKeyJoinFinding(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ReferencedTableQualifiedName,
    IReadOnlyList<ForeignKeyColumnPair> AllColumnPairs,
    IReadOnlyList<ForeignKeyColumnPair> MatchedColumnPairs,
    IReadOnlyList<ForeignKeyColumnPair> MissingColumnPairs,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
