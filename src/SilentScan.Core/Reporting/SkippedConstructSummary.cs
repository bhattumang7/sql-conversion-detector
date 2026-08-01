using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Reporting;

/// <summary>
/// Rolls the skip ledger up by construct kind, the way <see cref="DynamicSqlSummary"/> already
/// rolls up dynamic SQL outcomes (coverage-remediation-plan.md Phase 0.2) - a corpus scan can
/// produce thousands of individual <see cref="SkippedConstruct"/> entries, and without this
/// nothing in <see cref="ScanReport"/>'s own output answers "which unresolved constructs are
/// actually common here" without grepping the raw list by hand.
/// </summary>
public sealed record SkippedConstructSummary(
    int TotalCount,
    IReadOnlyDictionary<string, int> CountsByConstructKind)
{
    public static SkippedConstructSummary From(IReadOnlyList<SkippedConstruct> skippedConstructs)
    {
        var counts = skippedConstructs
            .GroupBy(entry => entry.ConstructKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new SkippedConstructSummary(skippedConstructs.Count, counts);
    }
}
