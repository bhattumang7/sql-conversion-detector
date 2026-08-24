using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record CrossTableTypeDriftFinding(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ParentColumnName,
    string ParentTypeDisplay,
    string ReferencedTableQualifiedName,
    string ReferencedColumnName,
    string ReferencedTypeDisplay,
    bool CollationDiffers,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

