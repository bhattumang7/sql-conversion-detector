using SilentScan.Core.Diagnostics;
using SilentScan.Core.Rules;
using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public sealed record TypedPredicateFinding(
    Verdict Verdict,
    PredicateOperand.Column Column,
    PredicateOperand OtherOperand,
    string Operator,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int ColumnPosition,
    SourceSpan? DynamicSqlCallSite = null,
    string? UnknownReason = null,
    string? PredicateFragmentText = null,
    string? Fingerprint = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<TypedPredicateFinding>
{
    public SourceSpan Location => new(SourcePath, Line, ColumnPosition);
    int IRelocatableFinding<TypedPredicateFinding>.PositionColumn => ColumnPosition;

    TypedPredicateFinding IRelocatableFinding<TypedPredicateFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}

public sealed record PredicateExtractionResult(
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<CollationConflictFinding> CollationConflictFindings,
    IReadOnlyList<WriteLossFinding> WriteLossFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs,
    IReadOnlyList<OversizedParameterFinding> OversizedParameterFindings,
    IReadOnlyList<UnderLengthParameterFinding> UnderLengthParameterFindings,
    IReadOnlyList<AnsiPaddingMismatchFinding> AnsiPaddingMismatchFindings,
    IReadOnlyList<LocalVariablePredicateFinding> LocalVariablePredicateFindings,
    IReadOnlyList<FilteredIndexParameterMismatchFinding> FilteredIndexParameterMismatchFindings);
