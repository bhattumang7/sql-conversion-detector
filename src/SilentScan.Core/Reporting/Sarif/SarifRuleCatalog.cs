using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting.Sarif;

/// <summary>Stable SARIF rule IDs/descriptions, one per finding kind this tool produces.</summary>
public static class SarifRuleCatalog
{
    public const string DynamicSqlAnalyzedRuleId = "silentscan/dynamic-sql/analyzed";
    public const string DynamicSqlUnanalyzableRuleId = "silentscan/dynamic-sql/unanalyzable";
    public const string DynamicSqlInnerParseFailedRuleId = "silentscan/dynamic-sql/inner-parse-failed";
    public const string DynamicSqlPartiallyAnalyzedRuleId = "silentscan/dynamic-sql/partially-analyzed";
    public const string ExpressionDerivedRuleId = "silentscan/lineage/expression-derived-column";
    public const string CollationConflictRuleId = "silentscan/verdict/collation-conflict";
    public const string WriteLossUnicodeReplacementRuleId = "silentscan/write-loss/unicode-to-non-unicode";
    public const string WriteLossApproximateTruncationRuleId = "silentscan/write-loss/approximate-to-exact-truncation";
    public const string WriteLossNumericScaleNarrowingRuleId = "silentscan/write-loss/numeric-scale-narrowing";
    public const string WriteLossTemporalPrecisionLossRuleId = "silentscan/write-loss/temporal-precision-loss";
    public const string TvfFenceCorrelatedApplyRuleId = "silentscan/tvf-fence/correlated-apply";
    public const string TvfFenceNestedUnderViewOrTvfRuleId = "silentscan/tvf-fence/nested-under-view-or-tvf";
    public const string TvfFenceFromOrJoinRuleId = "silentscan/tvf-fence/from-or-join";
    public const string TvfFenceInsertExecRuleId = "silentscan/tvf-fence/insert-exec";
    public const string TvfFenceStandaloneRuleId = "silentscan/tvf-fence/standalone";
    public const string ScalarUdfPredicateInvocationRuleId = "silentscan/scalar-udf/in-predicate";
    public const string ScalarUdfNestedUnderViewOrTvfRuleId = "silentscan/scalar-udf/nested-under-view-or-tvf";
    public const string ScalarUdfSchemaDependencyRuleId = "silentscan/scalar-udf/in-computed-column-or-constraint";
    public const string ScalarUdfProjectionInvocationRuleId = "silentscan/scalar-udf/in-select-or-expression";
    public const string ColumnCollationDriftRuleId = "silentscan/catalog/column-collation-drift";
    public const string CrossTableTypeDriftRuleId = "silentscan/catalog/cross-table-fk-type-drift";

    public static string TvfFenceRuleId(TvfFenceFindingKind kind) => kind switch
    {
        TvfFenceFindingKind.CorrelatedApply => TvfFenceCorrelatedApplyRuleId,
        TvfFenceFindingKind.NestedUnderViewOrTvf => TvfFenceNestedUnderViewOrTvfRuleId,
        TvfFenceFindingKind.FromOrJoin => TvfFenceFromOrJoinRuleId,
        TvfFenceFindingKind.InsertExec => TvfFenceInsertExecRuleId,
        TvfFenceFindingKind.Standalone => TvfFenceStandaloneRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled TvfFenceFindingKind."),
    };

    public static string ScalarUdfRuleId(ScalarUdfFindingKind kind) => kind switch
    {
        ScalarUdfFindingKind.PredicateInvocation => ScalarUdfPredicateInvocationRuleId,
        ScalarUdfFindingKind.NestedUnderViewOrTvf => ScalarUdfNestedUnderViewOrTvfRuleId,
        ScalarUdfFindingKind.SchemaDependency => ScalarUdfSchemaDependencyRuleId,
        ScalarUdfFindingKind.ProjectionInvocation => ScalarUdfProjectionInvocationRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled ScalarUdfFindingKind."),
    };

    public static string WriteLossRuleId(WriteLossKind kind) => kind switch
    {
        WriteLossKind.UnicodeToNonUnicodeReplacement => WriteLossUnicodeReplacementRuleId,
        WriteLossKind.ApproximateToExactTruncation => WriteLossApproximateTruncationRuleId,
        WriteLossKind.NumericScaleNarrowing => WriteLossNumericScaleNarrowingRuleId,
        WriteLossKind.TemporalPrecisionLoss => WriteLossTemporalPrecisionLossRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled WriteLossKind."),
    };

    public static string DynamicSqlRuleId(DynamicSqlOutcome outcome) => outcome switch
    {
        DynamicSqlOutcome.AnalyzedLiteral => DynamicSqlAnalyzedRuleId,
        DynamicSqlOutcome.Unanalyzable => DynamicSqlUnanalyzableRuleId,
        DynamicSqlOutcome.InnerParseFailed => DynamicSqlInnerParseFailedRuleId,
        DynamicSqlOutcome.PartiallyAnalyzed => DynamicSqlPartiallyAnalyzedRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled DynamicSqlOutcome."),
    };

    public static string Tier1RuleId(SargabilityFindingKind kind) => kind switch
    {
        SargabilityFindingKind.FunctionWrappedColumn => "silentscan/tier1/function-wrapped-column",
        SargabilityFindingKind.CastOrConvertOnColumn => "silentscan/tier1/cast-or-convert-on-column",
        SargabilityFindingKind.ColumnArithmetic => "silentscan/tier1/column-arithmetic",
        SargabilityFindingKind.LeadingWildcardLike => "silentscan/tier1/leading-wildcard-like",
        SargabilityFindingKind.LikePatternNotLiteral => "silentscan/tier1/like-pattern-not-literal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled SargabilityFindingKind."),
    };

    public static string VerdictRuleId(Verdict verdict) => verdict switch
    {
        Verdict.ScanForced => "silentscan/verdict/scan-forced",
        Verdict.RangeSeek => "silentscan/verdict/range-seek",
        Verdict.Unknown => "silentscan/verdict/unknown",
        Verdict.SeekPreserved => "silentscan/verdict/seek-preserved",
        Verdict.OperandClash => "silentscan/verdict/operand-clash",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unhandled Verdict."),
    };

    /// <summary>
    /// The three <see cref="DynamicSqlOutcome"/> rule IDs are deliberately excluded from
    /// <see cref="RuleId"/> suffixing: <see cref="Predicates.DynamicSqlFinding"/> has no
    /// <see cref="FindingConfidence"/> field of its own - it reports the classification of an
    /// EXEC/sp_executesql call site, not a defect claim with confidence in the value of anything.
    /// </summary>
    private static readonly HashSet<string> DynamicSqlOutcomeRuleIds = new(StringComparer.Ordinal)
    {
        DynamicSqlAnalyzedRuleId,
        DynamicSqlUnanalyzableRuleId,
        DynamicSqlInnerParseFailedRuleId,
        DynamicSqlPartiallyAnalyzedRuleId,
    };

    /// <summary>
    /// The rule ID a finding reports under, given its own confidence - a High-confidence finding
    /// keeps the plain rule ID; anything less appends a confidence suffix so it stays
    /// independently filterable in CI (GitHub code scanning can allow/suppress by rule ID prefix)
    /// without disturbing the established <c>silentscan/&lt;family&gt;/&lt;name&gt;</c> scheme
    /// that <see cref="AllRules"/> and its golden test are built on. Never call this with
    /// <paramref name="baseRuleId"/> one of the <see cref="DynamicSqlOutcomeRuleIds"/> - those
    /// findings carry no confidence to suffix by.
    /// </summary>
    public static string RuleId(string baseRuleId, FindingConfidence confidence) => confidence switch
    {
        FindingConfidence.High => baseRuleId,
        FindingConfidence.Medium => $"{baseRuleId}/medium-confidence",
        FindingConfidence.Low => $"{baseRuleId}/low-confidence",
        _ => throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Unhandled FindingConfidence."),
    };

    public static IReadOnlyList<SarifRule> AllRules { get; } = BuildAllRules();

    private static IReadOnlyList<SarifRule> BuildAllRules()
    {
        SarifRule[] baseRules =
        [
            Rule(Tier1RuleId(SargabilityFindingKind.FunctionWrappedColumn), "A column is wrapped in a function call inside a predicate, preventing an index seek."),
            Rule(Tier1RuleId(SargabilityFindingKind.CastOrConvertOnColumn), "A column has CAST/CONVERT applied to it inside a predicate."),
            Rule(Tier1RuleId(SargabilityFindingKind.ColumnArithmetic), "A column has arithmetic applied to it inside a predicate."),
            Rule(Tier1RuleId(SargabilityFindingKind.LeadingWildcardLike), "A LIKE predicate on a column starts with a wildcard, forcing a full scan."),
            Rule(Tier1RuleId(SargabilityFindingKind.LikePatternNotLiteral), "A LIKE predicate's pattern is not a literal, so a leading wildcard can't be ruled out statically."),
            Rule(VerdictRuleId(Verdict.ScanForced), "An implicit type conversion on the column side forces a full scan."),
            Rule(VerdictRuleId(Verdict.RangeSeek), "An implicit type conversion on the column side permits only a dynamic range seek, not a direct seek."),
            Rule(VerdictRuleId(Verdict.Unknown), "A predicate's sargability could not be determined (e.g. unresolved collation) - never guessed."),
            Rule(VerdictRuleId(Verdict.SeekPreserved), "A predicate compares types where the seek is preserved (reported for completeness; not filtered into ScanReportBuilder's actionable findings)."),
            Rule(VerdictRuleId(Verdict.OperandClash), "The oracle-probed type matrix confirms this exact type pair does not compile as a comparison at all - a definitive fact, not an absence of probe data."),
            Rule(DynamicSqlAnalyzedRuleId, "A dynamic SQL call site with a provably-constant argument; its contents were reparsed and analyzed like static SQL."),
            Rule(DynamicSqlUnanalyzableRuleId, "A dynamic SQL call site whose argument depends on a variable, parameter, or expression and could not be statically analyzed."),
            Rule(DynamicSqlInnerParseFailedRuleId, "A dynamic SQL call site's argument was provably constant but its reassembled text did not parse as T-SQL."),
            Rule(DynamicSqlPartiallyAnalyzedRuleId, "A dynamic SQL call site's argument contained a value standing for a whole optional clause/fragment, not a single scalar; the surrounding, unaffected query structure was analyzed, but the elided fragment's own content was never examined."),
            Rule(ExpressionDerivedRuleId, "A predicate compares a column that is a CAST/CONVERT or other computed expression by the time it reaches this statement (introduced in this statement's own derived table, or upstream in a view/TVF's SELECT list) - no index seek is possible regardless of the comparison's types."),
            Rule(CollationConflictRuleId, "Two columns with genuinely different, incompatible collations are compared directly - this does not compile (SQL Server error 468, \"Cannot resolve the collation conflict\"), not a seek/scan question."),
            Rule(WriteLossUnicodeReplacementRuleId, "An INSERT/UPDATE assigns a Unicode (NVARCHAR/NCHAR) value to a non-Unicode (VARCHAR/CHAR) target - any character outside the target collation's codepage is silently replaced with '?', with no error."),
            Rule(WriteLossApproximateTruncationRuleId, "An INSERT/UPDATE assigns an approximate-numeric (REAL/FLOAT) value to an exact integer target - the fractional part is silently dropped, with no error."),
            Rule(WriteLossNumericScaleNarrowingRuleId, "An INSERT/UPDATE assigns a DECIMAL/NUMERIC value to a target with a smaller scale - digits past the target's scale are silently rounded away, with no error."),
            Rule(WriteLossTemporalPrecisionLossRuleId, "An INSERT/UPDATE assigns a DATETIME/DATETIME2/SMALLDATETIME/DATETIMEOFFSET value to a DATE target - the time-of-day component is silently dropped, with no error."),
            Rule(TvfFenceCorrelatedApplyRuleId, "A CROSS/OUTER APPLY calls a multi-statement or CLR table-valued function with an argument correlated to an outer row - the whole function body re-executes once per outer row, and interleaved execution (2017+) does not rescue this."),
            Rule(TvfFenceNestedUnderViewOrTvfRuleId, "A view or inline TVF referenced here is itself, transitively, built over a multi-statement/CLR TVF - the fence and its fabricated cardinality estimate are inherited invisibly through however many layers sit between."),
            Rule(TvfFenceFromOrJoinRuleId, "A FROM/JOIN references a multi-statement/CLR table-valued function directly - the optimizer cannot see into its body, so the reference carries a fixed cardinality estimate (1 row legacy CE / 100 rows 2014+ CE) that propagates into the surrounding plan."),
            Rule(TvfFenceInsertExecRuleId, "An INSERT ... EXEC forces the executed procedure's entire result set to be spooled to a worktable before insertion - the same fence family, reached from a procedure call rather than a function reference."),
            Rule(TvfFenceStandaloneRuleId, "A standalone SELECT references a multi-statement/CLR table-valued function with nothing else in the FROM clause - the fence and its fixed estimate are real, but there is no surrounding plan for the estimate to poison."),
            Rule(ScalarUdfPredicateInvocationRuleId, "A scalar UDF is called in a WHERE/JOIN ON/HAVING/MERGE ON predicate - per-row execution, non-sargable, and (pre-2019, or on any engine when the UDF proves non-inlineable) forces the whole plan serial. Distinct from a syntactic function-wrapped-column finding on the same predicate: this claim is catalog-proven per-row/serial cost, not sargability loss, and the two are reported independently by design."),
            Rule(ScalarUdfNestedUnderViewOrTvfRuleId, "A view or inline TVF referenced here calls a scalar UDF, transitively, somewhere in its own definition - the per-row cost (and, pre-2019, forced-serial plan) is inherited invisibly through however many layers sit between. Pre-2019 an inline TVF's expansion spreads the UDF into every caller; 2019+ a scalar-UDF call inside an iTVF is itself a FROID inlining-blocker interaction."),
            Rule(ScalarUdfSchemaDependencyRuleId, "A computed column, DEFAULT, or CHECK constraint definition calls a scalar UDF - this poisons every query that touches the table with per-row/serial cost, even one that never names the column, and is detected from the catalog alone."),
            Rule(ScalarUdfProjectionInvocationRuleId, "A scalar UDF is called outside any predicate (SELECT list, ORDER BY, GROUP BY, SET/variable assignment) - per-row execution and (pre-2019, or non-inlineable) a forced-serial plan, but sargability is unaffected."),
            Rule(ColumnCollationDriftRuleId, "A string-family column's own collation differs from the database's default collation (or, for a temp table/table variable, from tempdb's effective collation) - a conversion seed: any future comparison against a column/literal carrying the baseline collation risks a collation-conflict compile error or a forced-scan implicit conversion. Catalog-only, detected before any query reaches the column."),
            Rule(CrossTableTypeDriftRuleId, "A foreign-key column pair's declared types and/or collations genuinely differ - a conversion seed on every JOIN that follows this relationship, detected from the catalog alone (sys.foreign_key_columns), independent of whether any scanned query actually joins on it."),
        ];

        // Only the Medium variant is generated: nothing in this tool produces a Low-confidence
        // finding yet, and a rule entry with no possible producer would itself be the kind of
        // silent-until-someone-checks noise CLAUDE.md's "never silently counted as clean" warns
        // against - add the Low variant here the day a Low producer actually exists.
        var mediumVariants = baseRules
            .Where(rule => !DynamicSqlOutcomeRuleIds.Contains(rule.Id))
            .Select(rule => Rule(RuleId(rule.Id, FindingConfidence.Medium), $"(Medium confidence) {rule.ShortDescription.Text}"));

        return [.. baseRules, .. mediumVariants];
    }

    private static SarifRule Rule(string id, string description) => new(id, new SarifMessage(description));
}
