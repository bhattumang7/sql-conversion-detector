namespace SilentScan.Core.Predicates;

/// <summary>Everything <see cref="DynamicSqlValue.DynamicSqlScannerV2.Scan"/> found in one parsed file: definite unanalyzable findings, and candidate scripts ready for <see cref="DynamicSqlPipeline"/> to reparse.</summary>
public sealed record DynamicSqlExtractionResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<DynamicSqlScript> AnalyzableScripts,
    IReadOnlyList<ProcedureOutputSummary> OutputSummaries);
