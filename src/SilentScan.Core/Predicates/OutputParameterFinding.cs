namespace SilentScan.Core.Predicates;

public enum OutputParameterFindingKind
{
UnassignedOnSomePath,
}

public sealed record OutputParameterFinding(
    OutputParameterFindingKind Kind,
    string SourcePath,
    string ParameterName,
    int ProcedureLine,
    int ProcedureColumn,
    int UnresolvedExitLine,
    int UnresolvedExitColumn,
    FindingConfidence Confidence = FindingConfidence.High);
