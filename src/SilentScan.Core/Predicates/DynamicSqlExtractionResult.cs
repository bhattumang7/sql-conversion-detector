namespace SilentScan.Core.Predicates;

public sealed record DynamicSqlExtractionResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<DynamicSqlScript> AnalyzableScripts,
    IReadOnlyList<ProcedureOutputSummary> OutputSummaries);
