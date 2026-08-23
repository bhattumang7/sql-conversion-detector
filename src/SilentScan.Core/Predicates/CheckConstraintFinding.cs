using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum CheckConstraintFindingKind
{
NullNotHandled,

ConstraintOnIdentityColumn,
}

public sealed record CheckConstraintFinding(
    CheckConstraintFindingKind Kind,
    string ConstraintName,
    string TableQualifiedName,
    string ColumnName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

