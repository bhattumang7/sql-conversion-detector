using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record ProcCallArgumentMismatchFinding(
    string? CallerScopeQualifiedName,
    string CalleeQualifiedName,
    string FormalParameterName,
    string CallerVariableName,
    string CallerTypeDisplay,
    string FormalParameterTypeDisplay,
    WriteLossKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

