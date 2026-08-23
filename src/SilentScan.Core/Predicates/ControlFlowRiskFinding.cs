using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum ControlFlowRiskFindingKind
{
    CursorFetchColumnCountMismatch,

    EmptyCatchBlock,

    TriggerEmitsOutput,

    DirtyReadIsolationHint,

    DuplicatedCallArgument,

    LegacyIdentityIntrinsic,

    GotoUsage,

    CaseExpressionMissingElse,

    NonDeterministicCaseInput,
}

public sealed record ControlFlowRiskFinding(
    ControlFlowRiskFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

