using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record DanglingObjectReferenceFinding(
    string ModuleQualifiedName,
    string ModuleTypeDescription,
    string ReferencedEntityName,
    string? ReferencedSchemaName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
