using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record MultiReferencedCteFinding(
    string CteName,
    int ReferenceCount,
    IReadOnlyList<int> ReferenceLines,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

