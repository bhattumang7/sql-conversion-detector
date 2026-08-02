using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting;

public sealed record ScanReport(
    ParseHealthReport ParseHealth,
    IReadOnlyList<SargabilityFinding> Tier1Findings,
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<DynamicSqlFinding> DynamicSqlFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs,
    SkippedConstructSummary SkippedConstructSummary,
    TypedPredicateSummary TypedPredicateSummary,
    DynamicSqlSummary DynamicSqlSummary,
    int SchemaVersion = ScanReport.CurrentSchemaVersion)
{
    /// <summary>
    /// Bumped whenever a breaking change is made to this report's own shape or to any finding
    /// record it carries (a field renamed/removed, an enum member's meaning changed) - CLAUDE.md:
    /// "Findings schema is versioned JSON." Before this field existed, nothing in the emitted
    /// JSON let a consumer tell one tool version's output from another's.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
