using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum CodeMetricFindingKind
{
    LineTooLong,

    ModuleTooLong,

    RoutineTooLong,

    TooManyParameters,

    NestingTooDeep,

    TooManyConditionalOperators,

    TooManyCaseBranches,

    CaseBranchTooLong,
}

public sealed record CodeMetricFinding(
    CodeMetricFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    int MeasuredValue,
    int Threshold,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Low) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

