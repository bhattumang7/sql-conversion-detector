using SilentScan.Bench.Scenarios;
using SilentScan.Core.Rules;

namespace SilentScan.Bench.Reporting;

/// <summary>
/// One matched-vs-mismatched cell of the cost table (CLAUDE.md Benchmark protocol). Each metric
/// is the median of 5 warm runs. <paramref name="StaticVerdict"/> is what
/// <see cref="SilentScan.Core.Rules.VerdictClassifier"/> predicts for this exact type pair - stamped onto every
/// row so a reader can tell "this row confirms the classifier" from "this row contradicts it"
/// directly, instead of cross-referencing the matrix by hand against a bare Matched=false label.
/// </summary>
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
