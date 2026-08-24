using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum ForcedSerialFindingKind
{
    TableVariableModification,

    FastForwardCursor,

    NonParallelizableIntrinsic,
}

public sealed record ForcedSerialFinding(
    ForcedSerialFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

