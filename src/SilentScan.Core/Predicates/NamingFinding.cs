using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum NamingFindingKind
{
    ReservedKeywordAsIdentifier,

    SpPrefixOnUserRoutine,

    UnqualifiedCreate,

    RedundantTypeQualifier,
}

public sealed record NamingFinding(
    NamingFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

