using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum WindowFunctionArgumentFindingKind
{
    LagLeadNegativeOffset,
    PercentileOutOfRange,
}

public sealed record WindowFunctionArgumentFinding(
    WindowFunctionArgumentFindingKind Kind,
    string FunctionName,
    string ArgumentText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
