using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum TriggerCorrectnessFindingKind
{
    MultiRowUnsafeSingleRowAssignment,

    MultiRowUnsafeKeyedDml,

    NoEarlyOutForEmptyInvocation,

    DirectRecursiveTrigger,

    InsteadOfInsertFilteredNoRejectPath,

    UpdateFunctionWithoutValueComparison,

    LogonTriggerHostNameGate,
}

public sealed record TriggerCorrectnessFinding(
    TriggerCorrectnessFindingKind Kind,
    string TriggerQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

