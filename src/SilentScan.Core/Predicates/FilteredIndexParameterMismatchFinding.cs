namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md full-archive practitioner sweep §E, "Filtered index whose predicate
/// compares against a variable/parameter, not a literal" - a real query-site predicate compares a
/// column against a local variable or formal parameter (<see cref="VariableName"/>/<see
/// cref="IsFormalParameter"/>), where that SAME column carries a filtered index whose own filter
/// predicate is a simple <c>Column = Literal</c> equality (<see cref="IndexName"/>/<see
/// cref="FilterLiteralText"/>, extracted by <see
/// cref="IndexDesignScanner.TryExtractSimpleLiteralEqualityFilter"/>). Confirmed directly against
/// the standing Docker oracle (2026-08-18, <c>SET SHOWPLAN_XML</c>): a query filtering on the
/// literal (<c>WHERE Status = 'Active'</c>) used the filtered index via an Index Seek; the
/// IDENTICAL predicate through a parameter instead (<c>DECLARE @p VARCHAR(20) = 'Active'; ...
/// WHERE Status = @p</c>) did NOT use the filtered index at all - it fell back to a full Clustered
/// Index Scan, even though the parameter's runtime value was the exact same string as the index's
/// own filter literal. This is a structural, compile-time optimizer limitation, not a cardinality/
/// parameter-sniffing effect the runtime VALUE could ever fix - the filtered index is silently,
/// permanently unusable for this access path no matter what value the caller passes.
///
/// A genuinely different claim from the already-shipped <see
/// cref="IndexDesignFindingKind.FilterColumnNotInIndex"/> (a filtered index's own filter references
/// a column absent from its own key/INCLUDE list - a covering-column gap, nothing to do with how a
/// query at a call site phrases its own predicate). Lives in its own finding type, not folded into
/// <see cref="IndexDesignFinding"/>, because unlike every kind on that type this is not a pure
/// catalog fact - it needs a real query-site predicate's resolved column/operand, the exact
/// resolution machinery <see cref="TypedPredicateExtractor"/> already builds for the conversion
/// stream (the same operand-classification machinery <see cref="LocalVariablePredicateFinding"/>
/// already reuses for its own, different claim) - reusing it here rather than rebuilding table/
/// column resolution from scratch a second time.
///
/// <see cref="FindingConfidence.High"/> always: the filter-literal match is exact text equality on
/// the reparsed filter definition (never a heuristic), and the "a parameter/variable operand can
/// never satisfy a filtered index match" rule is unconditional optimizer behavior, not a threshold
/// or estimation. Deliberately NEVER gated on an active <c>OPTION (RECOMPILE)</c>/<c>WITH
/// RECOMPILE</c> guard, unlike the sibling <see cref="LocalVariablePredicateFinding"/> extracted at
/// the same site: that finding's own claim is a cardinality-ESTIMATE risk RECOMPILE genuinely
/// resolves by re-sniffing the real value at each execution, but the filtered-index match rule this
/// finding reports is evaluated at COMPILE TIME against the predicate's own textual shape (literal
/// vs. parameter/variable) - a recompiled plan still cannot match a filtered index against a
/// parameter operand no matter what value that parameter holds, so RECOMPILE does not resolve this
/// risk at all and must not suppress it.
/// </summary>
public sealed record FilteredIndexParameterMismatchFinding(
    string TableQualifiedName,
    string ColumnName,
    string? IndexName,
    string FilterLiteralText,
    string VariableName,
    bool IsFormalParameter,
    string Operator,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
