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
    public const string ProcCallArgumentMismatchRuleId = "silentscan/call-graph/argument-type-mismatch";
    public const string TemporalBoundaryPrecisionRuleId = "silentscan/correctness/between-end-of-period-boundary";
    public const string MaxTypedColumnRuleId = "silentscan/catalog/max-typed-column";
    public const string OversizedParameterRuleId = "silentscan/predicates/oversized-parameter";
    public const string UnderLengthParameterRuleId = "silentscan/predicates/under-length-parameter";
    public const string AnsiPaddingMismatchRuleId = "silentscan/predicates/ansi-padding-mismatch";
    public const string CatchAllPredicateRuleId = "silentscan/predicates/catch-all-parameter";
    public const string LocalVariablePredicateRuleId = "silentscan/predicates/local-variable-predicate";
    public const string NotInNullableSubqueryRuleId = "silentscan/correctness/not-in-nullable-subquery";
    public const string NonUniqueUpdateSourceRuleId = "silentscan/correctness/nonunique-update-source";
    public const string ForcedSerialTableVariableModificationRuleId = "silentscan/forced-serial/table-variable-modification";
    public const string ForcedSerialFastForwardCursorRuleId = "silentscan/forced-serial/fast-forward-cursor";
    public const string ForcedSerialNonParallelizableIntrinsicRuleId = "silentscan/forced-serial/nonparallelizable-intrinsic";
    public const string UntrustedForeignKeyRuleId = "silentscan/catalog/untrusted-foreign-key";
    public const string UntrustedCheckConstraintRuleId = "silentscan/catalog/untrusted-check-constraint";
    public const string CascadingForeignKeyRuleId = "silentscan/catalog/cascading-foreign-key";
    public const string MultiReferencedCteRuleId = "silentscan/lineage/multi-referenced-cte";
    public const string NestedViewDepthRuleId = "silentscan/lineage/nested-view-depth";
    public const string PostExpansionJoinWidthRuleId = "silentscan/lineage/post-expansion-join-width";
    public const string SelectStarViewRuleId = "silentscan/lineage/select-star-view";
    public const string PartialCompositeForeignKeyJoinRuleId = "silentscan/join/partial-composite-fk";
    public const string ConcatenatedValueInConstantSqlRuleId = "silentscan/dynamic-sql/concatenated-value-in-constant-sql";
    public const string ExecStringConcatenatesParameterizableValueRuleId = "silentscan/dynamic-sql/exec-string-concatenates-parameterizable-value";
    public const string TempTableExecShapeColumnCountMismatchRuleId = "silentscan/dynamic-sql/insert-exec-temp-table-column-count-mismatch";
    public const string TempTableExecShapeColumnTypeMismatchRuleId = "silentscan/dynamic-sql/insert-exec-temp-table-column-type-mismatch";
    public const string NonPersistedComputedColumnRuleId = "silentscan/catalog/non-persisted-computed-column";

    public static string SetOptionRuleId(SetOptionFindingKind kind) => kind switch
    {
        SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature => "silentscan/set-option/quoted-identifier-off",
        SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature => "silentscan/set-option/numeric-roundabort-on",
        SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature => "silentscan/set-option/ansi-nulls-off",
        SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature => "silentscan/set-option/ansi-warnings-off",
        SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature => "silentscan/set-option/concat-null-yields-null-off",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled SetOptionFindingKind."),
    };

    public static string UnparameterizedDynamicSqlRuleId(UnparameterizedDynamicSqlFindingKind kind) => kind switch
    {
        UnparameterizedDynamicSqlFindingKind.ConcatenatedValueInConstantSql => ConcatenatedValueInConstantSqlRuleId,
        UnparameterizedDynamicSqlFindingKind.ExecStringConcatenatesParameterizableValue => ExecStringConcatenatesParameterizableValueRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled UnparameterizedDynamicSqlFindingKind."),
    };

    public static string TempTableExecShapeRuleId(TempTableExecShapeFindingKind kind) => kind switch
    {
        TempTableExecShapeFindingKind.ColumnCountMismatch => TempTableExecShapeColumnCountMismatchRuleId,
        TempTableExecShapeFindingKind.ColumnTypeMismatch => TempTableExecShapeColumnTypeMismatchRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled TempTableExecShapeFindingKind."),
    };

    public static string ForcedSerialRuleId(ForcedSerialFindingKind kind) => kind switch
    {
        ForcedSerialFindingKind.TableVariableModification => ForcedSerialTableVariableModificationRuleId,
        ForcedSerialFindingKind.FastForwardCursor => ForcedSerialFastForwardCursorRuleId,
        ForcedSerialFindingKind.NonParallelizableIntrinsic => ForcedSerialNonParallelizableIntrinsicRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled ForcedSerialFindingKind."),
    };

    public static string UntrustedConstraintRuleId(UntrustedConstraintFindingKind kind) => kind switch
    {
        UntrustedConstraintFindingKind.ForeignKey => UntrustedForeignKeyRuleId,
        UntrustedConstraintFindingKind.CheckConstraint => UntrustedCheckConstraintRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled UntrustedConstraintFindingKind."),
    };

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
        SargabilityFindingKind.CaseFoldOnColumn => "silentscan/tier1/case-fold-on-column",
        SargabilityFindingKind.DateFunctionOnColumn => "silentscan/tier1/date-function-on-column",
        SargabilityFindingKind.CharindexOrLeftOnColumn => "silentscan/tier1/charindex-or-left-on-column",
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
            Rule(Tier1RuleId(SargabilityFindingKind.CaseFoldOnColumn), "UPPER/LOWER wraps a column inside a predicate, forcing a scan under any collation - remediation differs by whether the column's real collation is case-sensitive."),
            Rule(Tier1RuleId(SargabilityFindingKind.DateFunctionOnColumn), "A date-part function (YEAR/MONTH/DAY/DATEPART/DATEDIFF/DATEADD/DATENAME) wraps a column inside a predicate, forcing a scan - a sargable rewrite (a literal date range instead) usually restores the seek."),
            Rule(Tier1RuleId(SargabilityFindingKind.CharindexOrLeftOnColumn), "CHARINDEX(x, col) or LEFT(col, n) wraps a column inside a predicate - a prefix-match shape (CHARINDEX(...) = 1, or LEFT(col, n) = 'x' with LEN('x') = n) is exactly rewritable to col LIKE 'x%'; any other shape is a genuine substring search with no sargable rewrite."),
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
            Rule(ProcCallArgumentMismatchRuleId, "A real EXEC call site passes a caller-side variable whose declared type risks silent data loss against the callee's own declared parameter type - an assignment-shaped conversion at parameter marshalling, not a predicate, classified the same way an INSERT/UPDATE assignment's silent data loss is."),
            Rule(TemporalBoundaryPrecisionRuleId, "A BETWEEN predicate's upper bound literal has fewer fractional-second digits than the TIME/DATETIME2/DATETIMEOFFSET column's own declared precision - a correctness bug, not a sargability one: rows in the precision gap are silently excluded, oracle-confirmed. Rewrite as >= start AND < (start of the next period) instead."),
            Rule(MaxTypedColumnRuleId, "A string/binary column is declared MAX-typed - it can never be an index key column at all, so any predicate/join on it can never seek regardless of how it's used. Catalog-only structural fact."),
            Rule(OversizedParameterRuleId, "A predicate compares a column against a parameter/variable/expression declared with a meaningfully longer length than the column itself - risks memory-grant inflation once the value feeds a sort/hash operator. Structural report, not a plan-shape claim for this specific predicate."),
            Rule(UnderLengthParameterRuleId, "A predicate compares a column against a parameter/variable/expression declared with a meaningfully shorter length than the column itself, or with no explicit length at all (T-SQL defaults to length 1) - the value is silently truncated before the predicate ever runs, changing which rows match or matching none. Structural report, same severity tier as WriteLossFinding's identical class of concern."),
            Rule(AnsiPaddingMismatchRuleId, "A LIKE predicate compares a non-ANSI-padded varchar/varbinary column against a literal pattern with significant trailing whitespace - the column can never store a value ending in whitespace at all (stripped at INSERT time under ANSI_PADDING OFF), so the pattern can never match anything the column could ever contain. Data-semantics finding, not a plan-shape one."),
            Rule(CatchAllPredicateRuleId, "A predicate of the shape (Col = @p OR @p IS NULL) - the 'catch-all'/'kitchen-sink' optional-filter idiom. One cached plan must stay correct for every possible NULL/non-NULL state of @p, which typically forces a scan regardless of what value is actually passed. Suppressed when the statement carries OPTION (RECOMPILE) or the procedure is WITH RECOMPILE, both of which let the optimizer see the real value on each call."),
            Rule(LocalVariablePredicateRuleId, "A predicate compares a column against a DECLARE'd local variable's value, never a formal parameter - the value is invisible to the cardinality estimator, which falls back to the column's average-density statistic instead of a value-specific estimate. The predicate is still fully sargable; only the row-count ESTIMATE is at risk, not the access path. Suppressed when the statement carries OPTION (RECOMPILE) or the procedure is WITH RECOMPILE."),
            Rule(NotInNullableSubqueryRuleId, "WHERE x NOT IN (SELECT y FROM t) where y is a nullable column - a three-valued-logic correctness trap. The instant the subquery produces one NULL row, the whole NOT IN evaluates to UNKNOWN for every outer row, so the query silently returns zero rows instead of the expected anti-join result. Never fires when the subquery column is NOT NULL, or when the subquery already filters it with an unconditional WHERE y IS NOT NULL."),
            Rule(NonUniqueUpdateSourceRuleId, "UPDATE ... FROM ... JOIN where the joined source's own join columns carry no unique index/constraint - if a target row matches more than one source row, SQL Server silently picks a value from an unspecified one of them (plan-dependent, not guaranteed stable across executions). MERGE raises a hard error in this exact situation instead of picking silently. Never fires when the source's join columns are covered by a genuine unique index/constraint, or when the SET clause never reads from the non-unique source."),
            Rule(ForcedSerialTableVariableModificationRuleId, "A DECLARE'd table variable is the write target of an INSERT/UPDATE/DELETE/MERGE, or the INTO target of an OUTPUT clause - the engine forces that one statement's own plan serial (effective MAXDOP 1), confirmed as NonParallelPlanReason=\"TableVariableTransactionsDoNotSupportParallelNestedTransaction\" in a real executed plan. A read-only reference to the same table variable is unaffected."),
            Rule(ForcedSerialFastForwardCursorRuleId, "A cursor declared FAST_FORWARD (or the equivalent bare FORWARD_ONLY READ_ONLY without an explicit STATIC/KEYSET/DYNAMIC) forces the cursor's own defining query plan serial, confirmed as NonParallelPlanReason=\"NoParallelFastForwardCursor\". This is the opposite of the common 'always use LOCAL FAST_FORWARD' fetch-overhead advice - that advice is still correct for row-by-row fetch cost, but it is specifically what defeats a parallel plan for the cursor's defining SELECT."),
            Rule(ForcedSerialNonParallelizableIntrinsicRuleId, "One of a finite, oracle-confirmed list of intrinsic functions/globals (OBJECT_ID, IDENT_CURRENT, ERROR_NUMBER, ERROR_MESSAGE, ERROR_LINE, ERROR_SEVERITY, ERROR_STATE, ERROR_PROCEDURE, @@TRANCOUNT) referenced inside a query with a real FROM clause forces that query's plan serial, confirmed as NonParallelPlanReason=\"NonParallelizableIntrinsicFunction\"."),
            Rule(UntrustedForeignKeyRuleId, "A foreign key the engine itself does not trust (sys.foreign_keys.is_not_trusted) - almost always the result of a WITH NOCHECK re-enabling ALTER TABLE statement. Forfeits join-elimination and other constraint-based query rewrites for every query that touches it."),
            Rule(UntrustedCheckConstraintRuleId, "A CHECK constraint the engine itself does not trust (sys.check_constraints.is_not_trusted) - almost always the result of a WITH NOCHECK re-enabling ALTER TABLE statement. The constraint may not actually hold over existing rows, and the optimizer forfeits constraint-based rewrites that assume it does."),
            Rule(CascadingForeignKeyRuleId, "A foreign key with a non-NO_ACTION ON DELETE/ON UPDATE action - a single DML statement against the referenced table silently cascades to every dependent row in the child table too, with no visible predicate change at the call site."),
            Rule(MultiReferencedCteRuleId, "A CTE referenced 2+ times downstream of its own WITH clause - SQL Server does not materialize a plain CTE once and reuse it, so each reference independently re-runs the CTE's own defining query. A self-reference inside a recursive CTE's own body is never counted - that is the structurally mandated recursion mechanism, not optional re-invocation."),
            Rule(NestedViewDepthRuleId, "A view/inline TVF nested 2+ view/TVF layers deep before reaching a base table - a change to a base table now has to be traced through 2+ independent view layers before its blast radius is understood, and each layer is a place a SELECT */column-list mismatch or silent type widening can hide."),
            Rule(PostExpansionJoinWidthRuleId, "A query whose written FROM/JOIN table count meaningfully understates how many base tables it actually touches once every view/inline-TVF reference is expanded transitively - a query that looks like a 3-table join can expand to 20."),
            Rule(SelectStarViewRuleId, "A view/inline TVF nested 1+ view/TVF layers deep whose own outermost SELECT is a bare or qualified * - its column list is frozen at CREATE/ALTER time and silently disagrees with the base table after any change, confirmed to survive even a live describe-only probe and real execution until sp_refreshview runs. Only fires when a real consuming query elsewhere explicitly selects a strict, named subset of the view's full column set - a consumer that itself does SELECT * never narrows anything and is never matched."),
            Rule(ConcatenatedValueInConstantSqlRuleId, "A proven-constant value was spliced into an EXEC/sp_executesql dynamic SQL string via concatenation rather than authored as one whole literal or passed through sp_executesql's own parameter mechanism - every distinct concatenated value compiles its own cached plan, oracle-confirmed against sys.dm_exec_cached_plans."),
            Rule(ExecStringConcatenatesParameterizableValueRuleId, "An EXEC(string)/EXEC(@sql) call site concatenates a proven-constant value into its SQL text - sp_executesql's own @params mechanism was available and unused, and would have let this call site reuse one cached plan across every distinct value instead of compiling a new one each time."),
            Rule(NonPersistedComputedColumnRuleId, "A computed column with is_persisted = 0 (sys.computed_columns) - its definition is recomputed from the base row on every read that touches it, independent of whether that definition calls a UDF at all. Catalog-only structural fact, never fires on a PERSISTED computed column regardless of whether it's also indexed."),
            Rule(TempTableExecShapeColumnCountMismatchRuleId, "INSERT INTO #temp EXEC proc, where the executed proc's real, engine-described result-set column count differs from #temp's own declared column count - INSERT ... EXEC binds purely by position, so this always raises a hard runtime error (Msg 213/8164) every time the statement executes, live-verified against sys.dm_exec_describe_first_result_set (compile-only)."),
            Rule(TempTableExecShapeColumnTypeMismatchRuleId, "INSERT INTO #temp EXEC proc, where column counts match but at least one position's type risks silent data loss between the executed proc's real, engine-described column type and #temp's own declared column type - a per-column WriteLossKind classification, live-verified against sys.dm_exec_describe_first_result_set (compile-only)."),
            Rule(PartialCompositeForeignKeyJoinRuleId, "A JOIN equates some but not all of a real composite foreign key's column pairs - the omitted column(s) let one parent row match more than one child row than the declared relationship allows, silently multiplying rows through the join. A correctness and plan defect, not a lost seek."),
            Rule(SetOptionRuleId(SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature), "The module was compiled under QUOTED_IDENTIFIER OFF (sys.sql_modules.uses_quoted_identifier) while its own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(SetOptionRuleId(SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature), "An explicit SET NUMERIC_ROUNDABORT ON in a module whose own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(SetOptionRuleId(SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature), "The module was compiled under ANSI_NULLS OFF (sys.sql_modules.uses_ansi_nulls) while its own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(SetOptionRuleId(SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature), "An explicit SET ANSI_WARNINGS OFF in a module whose own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(SetOptionRuleId(SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature), "An explicit SET CONCAT_NULL_YIELDS_NULL OFF in a module whose own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
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
