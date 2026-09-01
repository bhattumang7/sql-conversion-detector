using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting;

public sealed record ScanReport(
    ParseHealthReport ParseHealth,
    IReadOnlyDictionary<string, IReadOnlyList<IFinding>> FindingsByRuleId,
    IReadOnlyList<SkippedConstruct> SkippedConstructs,
    SkippedConstructSummary SkippedConstructSummary,
    TypedPredicateSummary TypedPredicateSummary,
    DynamicSqlSummary DynamicSqlSummary,
    int SchemaVersion = ScanReport.CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 78;

    public IReadOnlyList<RuleCatalogEntry> RuleCatalog { get; } = RuleCatalogEntries.All;

    public IReadOnlyList<TFinding> Find<TFinding>(string ruleId)
        where TFinding : IFinding =>
        FindingsByRuleId.TryGetValue(ruleId, out var findings) ? [.. findings.OfType<TFinding>()] : [];

    public ScanReport WithFindings(string ruleId, IReadOnlyList<IFinding> findings)
    {
        var updated = new Dictionary<string, IReadOnlyList<IFinding>>(FindingsByRuleId, StringComparer.Ordinal)
        {
            [ruleId] = findings,
        };
        return this with { FindingsByRuleId = updated };
    }
}
