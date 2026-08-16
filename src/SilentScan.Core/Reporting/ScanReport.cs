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
    IReadOnlyList<ScalarUdfFinding> ScalarUdfFindings,
    IReadOnlyList<ColumnCollationDriftFinding> ColumnCollationDriftFindings,
    IReadOnlyList<CrossTableTypeDriftFinding> CrossTableTypeDriftFindings,
    IReadOnlyList<ProcCallArgumentMismatchFinding> ProcCallArgumentMismatchFindings,
    IReadOnlyList<TemporalBoundaryPrecisionFinding> TemporalBoundaryFindings,
    IReadOnlyList<MaxTypedColumnFinding> MaxTypedColumnFindings,
    IReadOnlyList<OversizedParameterFinding> OversizedParameterFindings,
    IReadOnlyList<UnderLengthParameterFinding> UnderLengthParameterFindings,
    IReadOnlyList<PartialCompositeForeignKeyJoinFinding> PartialCompositeForeignKeyJoinFindings,
    IReadOnlyList<SetOptionFinding> SetOptionFindings,
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
    /// #2, MSTVF-as-fence). Bumped to 6 for the new <see cref="ScalarUdfFindings"/> stream
    /// (docs/detection-checklist.md Tier 1 #1, scalar UDF). Bumped to 7 for the new
    /// <see cref="ColumnCollationDriftFindings"/> stream (docs/detection-checklist.md Tier 1
    /// "Join-key and cross-object type/collation mismatch": column/temp-object collation !=
    /// database collation). Bumped to 8 for the new <see cref="CrossTableTypeDriftFindings"/>
    /// stream (same Tier 1 section: FK-linked cross-table type drift). Bumped to 9 for the new
    /// <see cref="ProcCallArgumentMismatchFindings"/> stream (same Tier 1 section: call-boundary
    /// argument mismatch). Bumped to 10 for the new <see cref="TemporalBoundaryFindings"/> stream
    /// (docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream": BETWEEN
    /// end-of-period boundary - a correctness finding, not a sargability one, but shares the same
    /// scope-resolution walk as Tier1Findings). Bumped to 11 for the new
    /// <see cref="MaxTypedColumnFindings"/> and <see cref="OversizedParameterFindings"/> streams
    /// (docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters"). Bumped to 12
    /// for the new <see cref="PartialCompositeForeignKeyJoinFindings"/> stream (docs/detection-
    /// checklist.md Tier 1 "Join predicate incomplete vs. the backing foreign key"). Bumped to 13
    /// for the new <see cref="SetOptionFindings"/> stream (docs/detection-checklist.md Tier 1
    /// "SET options that silently disable plan features"). Bumped to 14 for the new
    /// <see cref="UnderLengthParameterFindings"/> stream (docs/detection-checklist.md Tier 1
    /// "Under-length and length-defaulted string declarations").
    /// </summary>
    public const int CurrentSchemaVersion = 14;
}
