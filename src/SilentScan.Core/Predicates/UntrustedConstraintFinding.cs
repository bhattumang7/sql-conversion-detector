using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum UntrustedConstraintFindingKind
{
    ForeignKey,
    CheckConstraint,
}

public sealed record UntrustedConstraintFinding(
    UntrustedConstraintFindingKind Kind,
    string ConstraintName,
    string TableQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

