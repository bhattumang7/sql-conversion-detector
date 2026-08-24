using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum SelfReferencingDmlFindingKind
{
    DirectTableReference,
    ThroughView,
}

public sealed record SelfReferencingDmlFinding(
    SelfReferencingDmlFindingKind Kind,
    string StatementKind,
    string TargetTableQualifiedName,
    string ReadSideQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
