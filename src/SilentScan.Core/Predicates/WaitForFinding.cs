using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record WaitForFinding(
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    bool IsInsideTransaction,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

