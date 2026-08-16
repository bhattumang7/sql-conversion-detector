using SilentScan.Core.Diagnostics;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Pass 4 finding: a column-vs-other comparison, classified. CLAUDE.md's ranking fields -
/// depth (view layers between predicate and base column) and origin (where the predicate
/// is, and separately where a mismatch-introducing CAST layer lives, if any) - are carried
/// on <see cref="Column"/>'s provenance rather than duplicated here. The column's own resolved
/// type/collation and index name are likewise already reachable off <see cref="Column"/>
/// (<c>Column.Type</c>, <c>Column.IndexName</c>) and off <see cref="OtherOperand"/>'s own
/// <c>Type</c> - not duplicated as separate top-level fields. <paramref name="UnknownReason"/>,
/// <paramref name="PredicateFragmentText"/>, and <paramref name="Fingerprint"/> are the evidence
/// this record did NOT carry before: WHY (when <see cref="Verdict"/> is <see cref="Verdict.Unknown"/>),
/// the actual source text of the comparison, and a stable identity for tracking the same defect
/// across scans - respectively. <c>UnknownReason</c> is a short, stable reason code (<see
/// cref="Rules.VerdictClassifier.ClassifyWithReason"/>) explaining why <c>Verdict</c> is <see
/// cref="Verdict.Unknown"/> - null for every other verdict, never a guess invented to fill the
/// field. <c>PredicateFragmentText</c> is the comparison's own source text (e.g. <c>Col =
/// @Param</c>), rendered the same way <see cref="ExpressionDerivedFinding.PredicateFragmentText"/>
/// and <see cref="SargabilityFinding.PredicateFragmentText"/> already are. <c>Fingerprint</c> is a
/// short, stable hash (<see cref="TypedPredicateFindingIdentity.ComputeFingerprint"/>) of this
/// finding's table/column/operator/other-operand shape - the same identity <see
/// cref="Reporting.TypedFindingDeduplicator"/> already uses to collapse repeated occurrences, now
/// exposed so a consumer can track "the same defect" across two separate scans of the same repo
/// at the same commit without re-deriving the shape itself, independent of source location so an
/// unrelated edit elsewhere in the repo never changes it.
/// </summary>
public sealed record TypedPredicateFinding(
    Verdict Verdict,
    PredicateOperand.Column Column,
    PredicateOperand OtherOperand,
    string Operator,
    string SourcePath,
    int Line,
    int ColumnPosition,
    SourceSpan? DynamicSqlCallSite = null,
    string? UnknownReason = null,
    string? PredicateFragmentText = null,
    string? Fingerprint = null,
    FindingConfidence Confidence = FindingConfidence.High);

/// <summary>Everything <see cref="TypedPredicateExtractor.Extract"/> found in one parsed file: type-precedence verdicts, predicates that compare an expression-derived (CAST/computed) column rather than a real one, and predicates that don't even compile (a collation conflict between two real columns).</summary>
public sealed record PredicateExtractionResult(
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<CollationConflictFinding> CollationConflictFindings,
    IReadOnlyList<WriteLossFinding> WriteLossFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs,
    IReadOnlyList<OversizedParameterFinding> OversizedParameterFindings,
    IReadOnlyList<UnderLengthParameterFinding> UnderLengthParameterFindings,
    IReadOnlyList<AnsiPaddingMismatchFinding> AnsiPaddingMismatchFindings);
