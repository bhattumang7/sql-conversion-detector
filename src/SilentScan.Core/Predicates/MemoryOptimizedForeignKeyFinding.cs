using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum MemoryOptimizedForeignKeyFindingKind
{
    CrossStorageForeignKey,
    ReferentialAction,
}

public sealed record MemoryOptimizedForeignKeyFinding(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ReferencedTableQualifiedName,
    MemoryOptimizedForeignKeyFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}
