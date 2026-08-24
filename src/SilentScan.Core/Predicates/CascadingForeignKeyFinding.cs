using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public sealed record CascadingForeignKeyFinding(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ReferencedTableQualifiedName,
    ReferentialAction DeleteAction,
    ReferentialAction UpdateAction,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

