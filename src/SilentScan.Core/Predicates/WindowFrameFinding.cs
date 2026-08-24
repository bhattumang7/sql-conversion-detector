using System.Text.Json.Serialization;


namespace SilentScan.Core.Predicates;

public enum WindowFrameFindingKind
{
    ExplicitRangeFrame,

    ImplicitDefaultRangeFrame,
}

public sealed record WindowFrameFinding(
    WindowFrameFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

