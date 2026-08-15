using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting;

public sealed record ScanReport(
    ParseHealthReport ParseHealth,
    IReadOnlyList<SargabilityFinding> Tier1Findings,
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<DynamicSqlFinding> DynamicSqlFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<CollationConflictFinding> CollationConflictFindings,
    IReadOnlyList<WriteLossFinding> WriteLossFindings,
    IReadOnlyList<TvfFenceFinding> TvfFenceFindings,
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
    /// JSON let a consumer tell one tool version's output from another's. Bumped to 3 for the
    /// new WriteLossFindings stream (roadmap Phase E1). Bumped to 4 for the
    /// <see cref="Predicates.FindingConfidence"/> field added to every finding record - additive
    /// and defaulted to High, but a consumer that only checked schema version to decide whether
    /// a new field might exist deserves the same signal any other new field gets. Bumped to 5
    /// for the new <see cref="TvfFenceFindings"/> stream (docs/detection-checklist.md Tier 1
    /// #2, MSTVF-as-fence).
    /// </summary>
    public const int CurrentSchemaVersion = 5;
}
