using SilentScan.Bench.Scenarios;
using SilentScan.Core.Rules;

namespace SilentScan.Bench.Reporting;

public sealed record BenchmarkResult(
    string ScenarioName,
    int RowCount,
    bool LegacyCardinalityEstimation,
    bool Matched,
    QuerySelectivity Selectivity,
    long MedianLogicalReads,
    long MedianCpuMs,
    long MedianElapsedMs,
    Verdict StaticVerdict);
