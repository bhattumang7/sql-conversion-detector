using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum DeadCodeFindingKind
{
UnreachableCode,

UnusedLabel,

UnusedLocalVariable,

UnusedParameter,

RedundantJump,
}

public sealed record DeadCodeFinding(
    DeadCodeFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

