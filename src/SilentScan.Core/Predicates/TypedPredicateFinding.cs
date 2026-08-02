using SilentScan.Core.Diagnostics;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Pass 4 finding: a column-vs-other comparison, classified. CLAUDE.md's ranking fields -
/// depth (view layers between predicate and base column) and origin (where the predicate
/// is, and separately where a mismatch-introducing CAST layer lives, if any) - are carried
/// on <see cref="Column"/>'s provenance rather than duplicated here.
/// </summary>
public sealed record TypedPredicateFinding(
    Verdict Verdict,
    PredicateOperand.Column Column,
    PredicateOperand OtherOperand,
    string Operator,
    string SourcePath,
    int Line,
    int ColumnPosition,
    SourceSpan? DynamicSqlCallSite = null);

/// <summary>Everything <see cref="TypedPredicateExtractor.Extract"/> found in one parsed file: type-precedence verdicts, predicates that compare an expression-derived (CAST/computed) column rather than a real one, and predicates that don't even compile (a collation conflict between two real columns).</summary>
public sealed record PredicateExtractionResult(
    IReadOnlyList<TypedPredicateFinding> TypedFindings,
    IReadOnlyList<ExpressionDerivedFinding> ExpressionDerivedFindings,
    IReadOnlyList<CollationConflictFinding> CollationConflictFindings,
    IReadOnlyList<WriteLossFinding> WriteLossFindings,
    IReadOnlyList<SkippedConstruct> SkippedConstructs);
