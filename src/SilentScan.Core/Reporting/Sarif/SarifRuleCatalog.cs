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
    public const string ParameterReassignmentPredicateRuleId = "silentscan/predicates/reassigned-parameter";
    public const string CodeMetricLineTooLongRuleId = "silentscan/metrics/line-too-long";
    public const string CodeMetricModuleTooLongRuleId = "silentscan/metrics/module-too-long";
    public const string CodeMetricRoutineTooLongRuleId = "silentscan/metrics/routine-too-long";
    public const string CodeMetricTooManyParametersRuleId = "silentscan/metrics/too-many-parameters";
    public const string CodeMetricNestingTooDeepRuleId = "silentscan/metrics/nesting-too-deep";
    public const string CodeMetricTooManyConditionalOperatorsRuleId = "silentscan/metrics/too-many-conditional-operators";
    public const string CodeMetricTooManyCaseBranchesRuleId = "silentscan/metrics/too-many-case-branches";
    public const string CodeMetricCaseBranchTooLongRuleId = "silentscan/metrics/case-branch-too-long";
    public const string FormattingTabCharacterUsedRuleId = "silentscan/formatting/tab-character";
    public const string FormattingMultipleStatementsOnSameLineRuleId = "silentscan/formatting/multiple-statements-per-line";
    public const string FormattingMultipleDeclarationsOnSameLineRuleId = "silentscan/formatting/multiple-declarations-per-line";
    public const string FormattingMissingBeginEndBlockRuleId = "silentscan/formatting/missing-begin-end";
    public const string FormattingSingleLineConditionalBodyRuleId = "silentscan/formatting/single-line-conditional-body";
    public const string FormattingDanglingStatementAfterUnbracedBodyRuleId = "silentscan/formatting/dangling-statement-after-unbraced-body";
    public const string FormattingIfImmediatelyFollowingPriorBlockEndRuleId = "silentscan/formatting/if-following-prior-block-end";
    public const string FormattingRedundantParenthesesRuleId = "silentscan/formatting/redundant-parentheses";
    public const string FormattingMissingFileHeaderCommentRuleId = "silentscan/formatting/missing-file-header-comment";
    public const string NamingReservedKeywordAsIdentifierRuleId = "silentscan/naming/reserved-keyword-as-identifier";
    public const string NamingSpPrefixOnUserRoutineRuleId = "silentscan/naming/sp-prefix-on-user-routine";
    public const string NamingUnqualifiedCreateRuleId = "silentscan/naming/unqualified-create";
    public const string NamingRedundantTypeQualifierRuleId = "silentscan/naming/redundant-type-qualifier";
    public const string DeadCodeUnreachableCodeRuleId = "silentscan/dead-code/unreachable-code";
    public const string DeadCodeUnusedLabelRuleId = "silentscan/dead-code/unused-label";
    public const string DeadCodeUnusedLocalVariableRuleId = "silentscan/dead-code/unused-local-variable";
    public const string DeadCodeUnusedParameterRuleId = "silentscan/dead-code/unused-parameter";
    public const string DeadCodeRedundantJumpRuleId = "silentscan/dead-code/redundant-jump";
    public const string DuplicationCommentedOutCodeRuleId = "silentscan/duplication/commented-out-code";
    public const string DuplicationDuplicatedStringLiteralRuleId = "silentscan/duplication/duplicated-string-literal";
    public const string DuplicationSingleIterationLoopRuleId = "silentscan/duplication/single-iteration-loop";
    public const string DuplicationSelfAssignmentRuleId = "silentscan/duplication/self-assignment";
    public const string DuplicationIdenticalBinaryOperandsRuleId = "silentscan/duplication/identical-binary-operands";
    public const string DuplicationRepeatedUnaryOperatorRuleId = "silentscan/duplication/repeated-unary-operator";
    public const string DuplicationNegatedComparisonAsOppositeRuleId = "silentscan/duplication/negated-comparison-as-opposite";
    public const string DuplicationDuplicateSiblingConditionRuleId = "silentscan/duplication/duplicate-sibling-condition";
    public const string DuplicationIdenticalBranchBodiesRuleId = "silentscan/duplication/identical-branch-bodies";
    public const string DuplicationAllBranchesIdenticalRuleId = "silentscan/duplication/all-branches-identical";
    public const string DuplicationRedundantAndConditionRuleId = "silentscan/duplication/redundant-and-condition";
    public const string DuplicationMutuallyExclusiveAndConditionRuleId = "silentscan/duplication/mutually-exclusive-and-condition";
    public const string DuplicationCollapsibleNestedIfRuleId = "silentscan/duplication/collapsible-nested-if";
    public const string DuplicationNestedConditionalExpressionRuleId = "silentscan/duplication/nested-conditional-expression";
    public const string DuplicationAlwaysTrueOrFalseLiteralComparisonRuleId = "silentscan/duplication/always-true-or-false-literal-comparison";
    public const string DeprecatedSyntaxTaskCommentTodoRuleId = "silentscan/deprecated-syntax/task-comment-todo";
    public const string DeprecatedSyntaxTaskCommentFixmeRuleId = "silentscan/deprecated-syntax/task-comment-fixme";
    public const string DeprecatedSyntaxNonAnsiComparisonOperatorRuleId = "silentscan/deprecated-syntax/non-ansi-comparison-operator";
    public const string DeprecatedSyntaxEqualsNullComparisonRuleId = "silentscan/deprecated-syntax/equals-null-comparison";
    public const string DeprecatedSyntaxNotEqualsNullComparisonRuleId = "silentscan/deprecated-syntax/not-equals-null-comparison";
    public const string DeprecatedSyntaxLikeWithNoWildcardRuleId = "silentscan/deprecated-syntax/like-with-no-wildcard";
    public const string DeprecatedSyntaxLegacySystemCompatibilityViewRuleId = "silentscan/deprecated-syntax/legacy-system-compatibility-view";
    public const string DeprecatedSyntaxTableHintWithoutWithRuleId = "silentscan/deprecated-syntax/table-hint-without-with";
    public const string DeprecatedSyntaxNumberedProcedureDefinitionRuleId = "silentscan/deprecated-syntax/numbered-procedure-definition";
    public const string DeprecatedSyntaxNumberedProcedureExecutionRuleId = "silentscan/deprecated-syntax/numbered-procedure-execution";
    public const string DeprecatedSyntaxStringLiteralColumnAliasRuleId = "silentscan/deprecated-syntax/string-literal-column-alias";
    public const string DeprecatedSyntaxRemovedSecurityStoredProcedureRuleId = "silentscan/deprecated-syntax/removed-security-stored-procedure";
    public const string DeprecatedSyntaxDeprecatedSetRowcountRuleId = "silentscan/deprecated-syntax/deprecated-set-rowcount";
    public const string StatementShapeInsertWithoutColumnListRuleId = "silentscan/statement-shape/insert-without-column-list";
    public const string StatementShapeOrdinalOrderByRuleId = "silentscan/statement-shape/ordinal-order-by";
    public const string StatementShapeTopWithoutOrderByRuleId = "silentscan/statement-shape/top-without-order-by";
    public const string StatementShapeTableWithNoPrimaryKeyRuleId = "silentscan/statement-shape/table-with-no-primary-key";
    public const string StatementShapeMissingSetNocountOnRuleId = "silentscan/statement-shape/missing-set-nocount-on";
    public const string StatementShapeBareSelectStarRuleId = "silentscan/statement-shape/bare-select-star";
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
    public const string SelfReferencingDmlRuleId = "silentscan/dml/self-referencing";
    public const string TemporalTableHistoryIndexGapRuleId = "silentscan/catalog/temporal-history-index-gap";

    public static string ModuleCompileFlagRuleId(ModuleCompileFlagFindingKind kind) => kind switch
    {
        ModuleCompileFlagFindingKind.RecompilesEveryCall => "silentscan/catalog/with-recompile",
        ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation => "silentscan/catalog/tvf-return-database-collation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string WindowFrameRuleId(WindowFrameFindingKind kind) => kind switch
    {
        WindowFrameFindingKind.ExplicitRangeFrame => "silentscan/window-frame/explicit-range",
        WindowFrameFindingKind.ImplicitDefaultRangeFrame => "silentscan/window-frame/implicit-default-range",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public const string WaitForRuleId = "silentscan/control-flow/waitfor";

    public const string TransactionHygieneRuleId = "silentscan/control-flow/unresolved-transaction";
    public const string OutputParameterRuleId = "silentscan/control-flow/unassigned-output-parameter";

    public const string CompositeIndexLeadingColumnRuleId = "silentscan/index-shape/composite-leading-column-unconstrained";

    public static string IndexHintRuleId(IndexHintFindingKind kind) => kind switch
    {
        IndexHintFindingKind.IndexDoesNotExist => "silentscan/hint/index-does-not-exist",
        IndexHintFindingKind.HintedIndexNotSeekable => "silentscan/hint/index-not-seekable",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string SessionDateSettingRuleId(SessionDateSettingKind kind) => kind switch
    {
        SessionDateSettingKind.DateFormat => "silentscan/session-date/set-dateformat",
        SessionDateSettingKind.DateFirst => "silentscan/session-date/set-datefirst",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string CartesianJoinRuleId(CartesianJoinKind kind) => kind switch
    {
        CartesianJoinKind.CommaJoin => "silentscan/join/cartesian-comma-join",
        CartesianJoinKind.ExplicitCrossJoin => "silentscan/join/cartesian-cross-join",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string UndersizedDeclarationRuleId(UndersizedDeclarationSite site) => site switch
    {
        UndersizedDeclarationSite.TableColumn => "silentscan/declaration/undersized-column",
        UndersizedDeclarationSite.Declaration => "silentscan/declaration/undersized-variable-or-parameter",
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, null),
    };

    public const string TruncateSwallowedRuleId = "silentscan/control-flow/truncate-swallowed-by-catch";

    public static string DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind kind) => kind switch
    {
        DatabaseConfigurationFindingKind.PageVerifyNotChecksum => "silentscan/database/page-verify-not-checksum",
        DatabaseConfigurationFindingKind.AutoShrinkOn => "silentscan/database/auto-shrink-on",
        DatabaseConfigurationFindingKind.AutoCloseOn => "silentscan/database/auto-close-on",
        DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset => "silentscan/database/target-recovery-time-unset",
        DatabaseConfigurationFindingKind.QueryStoreNotReadWrite => "silentscan/database/query-store-not-read-write",
        DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto => "silentscan/database/query-store-capture-mode-not-auto",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string UnindexedTempTableUsageRuleId(UnindexedTempTableUsageKind kind) => kind switch
    {
        UnindexedTempTableUsageKind.JoinOperand => "silentscan/temp-table/unindexed-join-operand",
        UnindexedTempTableUsageKind.FilteredInWhere => "silentscan/temp-table/unindexed-where-filter",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string ViewOrderingRuleId(ViewOrderingFindingKind kind) => kind switch
    {
        ViewOrderingFindingKind.TopPercentOrderByNeverLimits => "silentscan/view/top-percent-order-by-no-op",
        ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer => "silentscan/view/order-by-not-guaranteed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

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

    public static string CodeMetricRuleId(CodeMetricFindingKind kind) => kind switch
    {
        CodeMetricFindingKind.LineTooLong => CodeMetricLineTooLongRuleId,
        CodeMetricFindingKind.ModuleTooLong => CodeMetricModuleTooLongRuleId,
        CodeMetricFindingKind.RoutineTooLong => CodeMetricRoutineTooLongRuleId,
        CodeMetricFindingKind.TooManyParameters => CodeMetricTooManyParametersRuleId,
        CodeMetricFindingKind.NestingTooDeep => CodeMetricNestingTooDeepRuleId,
        CodeMetricFindingKind.TooManyConditionalOperators => CodeMetricTooManyConditionalOperatorsRuleId,
        CodeMetricFindingKind.TooManyCaseBranches => CodeMetricTooManyCaseBranchesRuleId,
        CodeMetricFindingKind.CaseBranchTooLong => CodeMetricCaseBranchTooLongRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled CodeMetricFindingKind."),
    };

    public static string FormattingRuleId(FormattingFindingKind kind) => kind switch
    {
        FormattingFindingKind.TabCharacterUsed => FormattingTabCharacterUsedRuleId,
        FormattingFindingKind.MultipleStatementsOnSameLine => FormattingMultipleStatementsOnSameLineRuleId,
        FormattingFindingKind.MultipleDeclarationsOnSameLine => FormattingMultipleDeclarationsOnSameLineRuleId,
        FormattingFindingKind.MissingBeginEndBlock => FormattingMissingBeginEndBlockRuleId,
        FormattingFindingKind.SingleLineConditionalBody => FormattingSingleLineConditionalBodyRuleId,
        FormattingFindingKind.DanglingStatementAfterUnbracedBody => FormattingDanglingStatementAfterUnbracedBodyRuleId,
        FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd => FormattingIfImmediatelyFollowingPriorBlockEndRuleId,
        FormattingFindingKind.RedundantParentheses => FormattingRedundantParenthesesRuleId,
        FormattingFindingKind.MissingFileHeaderComment => FormattingMissingFileHeaderCommentRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled FormattingFindingKind."),
    };

    public static string NamingRuleId(NamingFindingKind kind) => kind switch
    {
        NamingFindingKind.ReservedKeywordAsIdentifier => NamingReservedKeywordAsIdentifierRuleId,
        NamingFindingKind.SpPrefixOnUserRoutine => NamingSpPrefixOnUserRoutineRuleId,
        NamingFindingKind.UnqualifiedCreate => NamingUnqualifiedCreateRuleId,
        NamingFindingKind.RedundantTypeQualifier => NamingRedundantTypeQualifierRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled NamingFindingKind."),
    };

    public static string DeadCodeRuleId(DeadCodeFindingKind kind) => kind switch
    {
        DeadCodeFindingKind.UnreachableCode => DeadCodeUnreachableCodeRuleId,
        DeadCodeFindingKind.UnusedLabel => DeadCodeUnusedLabelRuleId,
        DeadCodeFindingKind.UnusedLocalVariable => DeadCodeUnusedLocalVariableRuleId,
        DeadCodeFindingKind.UnusedParameter => DeadCodeUnusedParameterRuleId,
        DeadCodeFindingKind.RedundantJump => DeadCodeRedundantJumpRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled DeadCodeFindingKind."),
    };

    public static string DuplicationRuleId(DuplicationFindingKind kind) => kind switch
    {
        DuplicationFindingKind.CommentedOutCode => DuplicationCommentedOutCodeRuleId,
        DuplicationFindingKind.DuplicatedStringLiteral => DuplicationDuplicatedStringLiteralRuleId,
        DuplicationFindingKind.SingleIterationLoop => DuplicationSingleIterationLoopRuleId,
        DuplicationFindingKind.SelfAssignment => DuplicationSelfAssignmentRuleId,
        DuplicationFindingKind.IdenticalBinaryOperands => DuplicationIdenticalBinaryOperandsRuleId,
        DuplicationFindingKind.RepeatedUnaryOperator => DuplicationRepeatedUnaryOperatorRuleId,
        DuplicationFindingKind.NegatedComparisonAsOpposite => DuplicationNegatedComparisonAsOppositeRuleId,
        DuplicationFindingKind.DuplicateSiblingCondition => DuplicationDuplicateSiblingConditionRuleId,
        DuplicationFindingKind.IdenticalBranchBodies => DuplicationIdenticalBranchBodiesRuleId,
        DuplicationFindingKind.AllBranchesIdentical => DuplicationAllBranchesIdenticalRuleId,
        DuplicationFindingKind.RedundantAndCondition => DuplicationRedundantAndConditionRuleId,
        DuplicationFindingKind.MutuallyExclusiveAndCondition => DuplicationMutuallyExclusiveAndConditionRuleId,
        DuplicationFindingKind.CollapsibleNestedIf => DuplicationCollapsibleNestedIfRuleId,
        DuplicationFindingKind.NestedConditionalExpression => DuplicationNestedConditionalExpressionRuleId,
        DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison => DuplicationAlwaysTrueOrFalseLiteralComparisonRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled DuplicationFindingKind."),
    };

    public static string DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind kind) => kind switch
    {
        DeprecatedSyntaxFindingKind.TaskCommentTodo => DeprecatedSyntaxTaskCommentTodoRuleId,
        DeprecatedSyntaxFindingKind.TaskCommentFixme => DeprecatedSyntaxTaskCommentFixmeRuleId,
        DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator => DeprecatedSyntaxNonAnsiComparisonOperatorRuleId,
        DeprecatedSyntaxFindingKind.EqualsNullComparison => DeprecatedSyntaxEqualsNullComparisonRuleId,
        DeprecatedSyntaxFindingKind.NotEqualsNullComparison => DeprecatedSyntaxNotEqualsNullComparisonRuleId,
        DeprecatedSyntaxFindingKind.LikeWithNoWildcard => DeprecatedSyntaxLikeWithNoWildcardRuleId,
        DeprecatedSyntaxFindingKind.LegacySystemCompatibilityView => DeprecatedSyntaxLegacySystemCompatibilityViewRuleId,
        DeprecatedSyntaxFindingKind.TableHintWithoutWith => DeprecatedSyntaxTableHintWithoutWithRuleId,
        DeprecatedSyntaxFindingKind.NumberedProcedureDefinition => DeprecatedSyntaxNumberedProcedureDefinitionRuleId,
        DeprecatedSyntaxFindingKind.NumberedProcedureExecution => DeprecatedSyntaxNumberedProcedureExecutionRuleId,
        DeprecatedSyntaxFindingKind.StringLiteralColumnAlias => DeprecatedSyntaxStringLiteralColumnAliasRuleId,
        DeprecatedSyntaxFindingKind.RemovedSecurityStoredProcedure => DeprecatedSyntaxRemovedSecurityStoredProcedureRuleId,
        DeprecatedSyntaxFindingKind.DeprecatedSetRowcount => DeprecatedSyntaxDeprecatedSetRowcountRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled DeprecatedSyntaxFindingKind."),
    };

    public static string StatementShapeRuleId(StatementShapeFindingKind kind) => kind switch
    {
        StatementShapeFindingKind.InsertWithoutColumnList => StatementShapeInsertWithoutColumnListRuleId,
        StatementShapeFindingKind.OrdinalOrderBy => StatementShapeOrdinalOrderByRuleId,
        StatementShapeFindingKind.TopWithoutOrderBy => StatementShapeTopWithoutOrderByRuleId,
        StatementShapeFindingKind.TableWithNoPrimaryKey => StatementShapeTableWithNoPrimaryKeyRuleId,
        StatementShapeFindingKind.MissingSetNocountOn => StatementShapeMissingSetNocountOnRuleId,
        StatementShapeFindingKind.BareSelectStar => StatementShapeBareSelectStarRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled StatementShapeFindingKind."),
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
            Rule(SelfReferencingDmlRuleId, "An INSERT/UPDATE/DELETE/MERGE whose own read side (a self-join, a WHERE/SET subquery, or a view over the same base table) also names the exact table it writes to - oracle-confirmed to force extra defensive plan work (an Eager Spool for INSERT/DELETE, an extra Sort for UPDATE ... FROM/MERGE) that an otherwise-identical statement reading a different table never pays."),
            Rule(TemporalTableHistoryIndexGapRuleId, "A system-versioned temporal table's CURRENT side carries a nonclustered index with no structurally matching index (same key columns, same order) on its HISTORY side - oracle-confirmed that FOR SYSTEM_TIME AS OF/BETWEEN rewrites to a UNION ALL of the two tables, so a predicate that seeks the current-table branch via this index degrades to a full scan of the history-table branch. PRIMARY KEY/UNIQUE-constraint indexes are never compared - the engine itself forbids either constraint on a temporal history table."),
            Rule(ModuleCompileFlagRuleId(ModuleCompileFlagFindingKind.RecompilesEveryCall), "The module was authored WITH RECOMPILE (sys.sql_modules.is_recompiled) - every execution compiles a fresh plan and discards it immediately rather than caching it, so the module's own cost never accumulates in the plan cache at all, invisible to any monitoring that reads sys.dm_exec_cached_plans/sys.dm_exec_query_stats."),
            Rule(ModuleCompileFlagRuleId(ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation), "A non-schema-bound table-valued function's own RETURNS @t TABLE(...) declares a character-typed column with no explicit COLLATE clause, so its collation was implicitly resolved against the CURRENT database's default collation at CREATE/ALTER time and baked in (sys.sql_modules.uses_database_collation) - a later ALTER DATABASE ... COLLATE silently leaves the function's already-compiled return shape disagreeing with the database's new default. Schema-bound modules are excluded from this finding: oracle-confirmed that schema-binding sets this flag unconditionally regardless of whether the module touches string data at all, so it carries no differentiating signal there."),
            Rule(WindowFrameRuleId(WindowFrameFindingKind.ExplicitRangeFrame), "A window function's OVER clause uses an explicit RANGE frame - oracle-confirmed to cost materially more CPU at the Window Spool operator than the equivalent ROWS frame (peer-group value comparison vs. plain physical-offset counting), though both compile to the same Window Spool physical operator, not an on-disk-vs-not distinction."),
            Rule(WindowFrameRuleId(WindowFrameFindingKind.ImplicitDefaultRangeFrame), "A window function's OVER clause has an ORDER BY but no explicit frame clause at all - T-SQL silently defaults this to RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW, oracle-confirmed to carry the identical measured cost as an explicit RANGE frame, invisible in the source text."),
            Rule(WaitForRuleId, "WAITFOR DELAY/WAITFOR TIME holds the calling worker thread idle for the full delay/until-time - a documented, unconditional cost contributing to worker-pool exhaustion under load, and (inside an open transaction) extended lock hold duration."),
            Rule(ViewOrderingRuleId(ViewOrderingFindingKind.TopPercentOrderByNeverLimits), "A view/inline TVF's own outermost query uses TOP (100) PERCENT ... ORDER BY - oracle-confirmed provably meaningless: 100 PERCENT never excludes a row, so the ORDER BY exists purely to satisfy T-SQL's own view-ordering grammar rule (Msg 1033) and is not guaranteed to any consumer that doesn't apply its own ORDER BY."),
            Rule(ViewOrderingRuleId(ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer), "A view/inline TVF's own outermost query uses a genuinely row-limiting TOP (N) or OFFSET ... FETCH together with ORDER BY - the ORDER BY does decide which rows survive, but the FINAL output order is still not guaranteed to a consumer that doesn't apply its own ORDER BY, oracle-observed to sometimes still appear ordered purely as a plan-shape coincidence."),
            Rule(TransactionHygieneRuleId, "A BEGIN TRANSACTION reaches a RETURN/THROW, or the natural end of the module body, on some statically reachable path with no intervening COMMIT/ROLLBACK - oracle-confirmed directly: SQL Server itself raises Msg 266 (\"Transaction count after EXECUTE indicates a mismatching number of BEGIN and COMMIT statements\") and leaves the calling session's @@TRANCOUNT elevated by one the instant such a procedure returns, holding that transaction's locks indefinitely."),
            Rule(CompositeIndexLeadingColumnRuleId, "A real composite index's leading key column is never bound anywhere in the statement while the query genuinely constrains one of its NON-leading key columns - a composite index is a single B-tree keyed first by its leading column, so with no bound on that column this specific index cannot be seek-used for this predicate at all. Only fires when no OTHER usable index on the table leads with the same violating column either, so a table with a real alternative seek path is never flagged."),
            Rule(IndexHintRuleId(IndexHintFindingKind.IndexDoesNotExist), "An INDEX(...) table hint names an index that does not exist in the catalog for this table - oracle-confirmed a hard compile error (Msg 308) every time this statement runs, not a silently-ignored hint."),
            Rule(IndexHintRuleId(IndexHintFindingKind.HintedIndexNotSeekable), "An INDEX(...) table hint names a real index whose own leading key column is never bound anywhere in the statement - the hint forces the engine to use this specific index (not merely suggest it), and with no bound on the leading key the engine cannot descend its B-tree to a useful starting point, oracle-confirmed to degrade the forced access path to a full index scan."),
            Rule(SessionDateSettingRuleId(SessionDateSettingKind.DateFormat), "SET DATEFORMAT appears in this module's own body - oracle-confirmed the identical ambiguous string date literal ('03/04/2026') resolves to a different real date (March 4 vs. April 3) depending purely on which DATEFORMAT value was set first in the session, independent of the caller's own settings."),
            Rule(SessionDateSettingRuleId(SessionDateSettingKind.DateFirst), "SET DATEFIRST appears in this module's own body - oracle-confirmed DATEPART(weekday, ...) for a fixed real date returns a different ordinal depending purely on which DATEFIRST value was set first in the session."),
            Rule(CartesianJoinRuleId(CartesianJoinKind.CommaJoin), "A legacy comma-join (FROM A, B) with no predicate anywhere in the statement - no ON clause, no WHERE clause - connecting the two tables at all: a true cartesian product, the classic 'forgot the join condition' defect."),
            Rule(CartesianJoinRuleId(CartesianJoinKind.ExplicitCrossJoin), "An explicit CROSS JOIN with no predicate anywhere in the statement connecting the two tables - the author wrote CROSS JOIN, self-documenting a deliberate cartesian product, but this is still worth surfacing since an accidentally-left CROSS JOIN is a real, if less common, mistake."),
            Rule(UndersizedDeclarationRuleId(UndersizedDeclarationSite.TableColumn), "A real table column is declared with a string/binary length of 1 or 2 - almost always a truncated-from-a-larger-source mistake or a leftover single-character-flag placeholder that later grew real string content. Advisory only, no compared column needed - distinct from the under-length-vs-compared-column stream."),
            Rule(UndersizedDeclarationRuleId(UndersizedDeclarationSite.Declaration), "A DECLARE'd local variable or a procedure/function formal parameter is declared with a string/binary length of 1 or 2 - the same advisory-only, no-compared-column-needed claim as the table-column sibling."),
            Rule(TruncateSwallowedRuleId, "TRUNCATE TABLE sits inside a TRY block whose CATCH block never THROWs/RAISERRORs anywhere in its own statement tree - oracle-confirmed a real TRUNCATE failure (e.g. an enforced FK reference, Msg 4712) is silently swallowed here, with execution continuing as if the TRUNCATE had succeeded and no error reaching the caller. TRY with no matching CATCH at all is a hard parse error (Msg 102) and can never occur in valid T-SQL, correcting this item's own original framing."),
            Rule(UnindexedTempTableUsageRuleId(UnindexedTempTableUsageKind.JoinOperand), "A SELECT...INTO #temp table is later used as a JOIN operand in the same batch/procedure scope, but no index was ever created on it - oracle-confirmed this forces a full scan/Hash Match of the entire temp table, with no seek alternative possible at all without an index."),
            Rule(UnindexedTempTableUsageRuleId(UnindexedTempTableUsageKind.FilteredInWhere), "A SELECT...INTO #temp table is later filtered by a WHERE predicate in the same batch/procedure scope, but no index was ever created on it - the same no-seek-possible cost as the JOIN-operand sibling."),
            Rule(OutputParameterRuleId, "A procedure's own OUTPUT parameter is not assigned on some statically reachable path (a RETURN, or the natural end of the body) - oracle-confirmed a caller's own variable is left completely unchanged by the call on that path (not reset to NULL), so a reused caller variable can silently carry stale data from a previous, unrelated call."),
            Rule(DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.PageVerifyNotChecksum), "PAGE_VERIFY is not CHECKSUM - silent storage-level page corruption can go undetected until a much later, harder-to-diagnose failure."),
            Rule(DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.AutoShrinkOn), "AUTO_SHRINK is ON - a well-known, severe anti-pattern: constant fragmentation churn as the engine shrinks the file and the workload immediately re-grows it."),
            Rule(DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.AutoCloseOn), "AUTO_CLOSE is ON - the database's connection/buffer-pool state is torn down after the last connection closes and rebuilt from scratch on the next one."),
            Rule(DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset), "TARGET_RECOVERY_TIME is 0 (disabled) - indirect checkpoint is off; confirmed directly against a freshly created database on the same engine that the modern default is 60 seconds, not 0."),
            Rule(DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite), "Query Store is not actively running (actual state is not READ_WRITE) - informational: a real operational choice, not a universal anti-pattern."),
            Rule(DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto), "Query Store is running with a capture mode other than AUTO - informational: ALL is a deliberate, real choice some teams prefer for active troubleshooting."),
            Rule(TempTableExecShapeColumnCountMismatchRuleId, "INSERT INTO #temp EXEC proc, where the executed proc's real, engine-described result-set column count differs from #temp's own declared column count - INSERT ... EXEC binds purely by position, so this always raises a hard runtime error (Msg 213/8164) every time the statement executes, live-verified against sys.dm_exec_describe_first_result_set (compile-only)."),
            Rule(TempTableExecShapeColumnTypeMismatchRuleId, "INSERT INTO #temp EXEC proc, where column counts match but at least one position's type risks silent data loss between the executed proc's real, engine-described column type and #temp's own declared column type - a per-column WriteLossKind classification, live-verified against sys.dm_exec_describe_first_result_set (compile-only)."),
            Rule(PartialCompositeForeignKeyJoinRuleId, "A JOIN equates some but not all of a real composite foreign key's column pairs - the omitted column(s) let one parent row match more than one child row than the declared relationship allows, silently multiplying rows through the join. A correctness and plan defect, not a lost seek."),
            Rule(SetOptionRuleId(SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature), "The module was compiled under QUOTED_IDENTIFIER OFF (sys.sql_modules.uses_quoted_identifier) while its own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(SetOptionRuleId(SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature), "An explicit SET NUMERIC_ROUNDABORT ON in a module whose own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(SetOptionRuleId(SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature), "The module was compiled under ANSI_NULLS OFF (sys.sql_modules.uses_ansi_nulls) while its own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(SetOptionRuleId(SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature), "An explicit SET ANSI_WARNINGS OFF in a module whose own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(SetOptionRuleId(SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature), "An explicit SET CONCAT_NULL_YIELDS_NULL OFF in a module whose own body touches a filtered index or an indexed view - the optimizer cannot use either under this setting, so it silently falls back to a base-table/heap scan."),
            Rule(ParameterReassignmentPredicateRuleId, "A formal parameter is reassigned (SET/SELECT) on every statically reachable path before a later predicate use of the same name - the optimizer's compile-time SNIFFED value (from the caller's original argument) is provably stale by the time this predicate executes, unlike a plain DECLARE'd local (which was never sniffable at all). The predicate is still fully sargable; only the row-count ESTIMATE built from the sniffed value is at risk. Suppressed when the statement carries OPTION (RECOMPILE) or the procedure is WITH RECOMPILE."),
            Rule(CodeMetricLineTooLongRuleId, "A physical source line exceeds the configured maximum character length. Purely a readability signal - no query result or plan is affected."),
            Rule(CodeMetricModuleTooLongRuleId, "A module (or, in file-mode, a source file) exceeds the configured maximum line count. Purely a maintainability signal - no query result or plan is affected."),
            Rule(CodeMetricRoutineTooLongRuleId, "A procedure/function/trigger body exceeds the configured maximum line count. Purely a maintainability signal - no query result or plan is affected."),
            Rule(CodeMetricTooManyParametersRuleId, "A procedure/function declares more formal parameters than the configured maximum. Purely a maintainability signal - no query result or plan is affected."),
            Rule(CodeMetricNestingTooDeepRuleId, "An IF/WHILE/TRY nests more than the configured maximum depth inside a routine. Purely a readability signal - no query result or plan is affected."),
            Rule(CodeMetricTooManyConditionalOperatorsRuleId, "A single IF/WHILE condition chains more AND/OR operators than the configured maximum. Purely a readability signal - no query result or plan is affected."),
            Rule(CodeMetricTooManyCaseBranchesRuleId, "A single CASE expression has more WHEN branches than the configured maximum. Purely a maintainability signal - no query result or plan is affected."),
            Rule(CodeMetricCaseBranchTooLongRuleId, "A single CASE WHEN branch's result expression spans more lines than the configured maximum. Purely a readability signal - no query result or plan is affected."),
            Rule(FormattingTabCharacterUsedRuleId, "A literal tab character appears in the source text. Purely a readability signal - no query result or plan is affected."),
            Rule(FormattingMultipleStatementsOnSameLineRuleId, "Two or more statements in the same block start on the same physical source line. Purely a readability signal - no query result or plan is affected."),
            Rule(FormattingMultipleDeclarationsOnSameLineRuleId, "Two or more variables in the same DECLARE are declared on the same physical source line. Purely a readability signal - no query result or plan is affected."),
            Rule(FormattingMissingBeginEndBlockRuleId, "An IF/WHILE/ELSE body is a single statement with no BEGIN...END - a later statement added here without braces silently falls outside the conditional. Purely a maintainability risk - no query result or plan is affected."),
            Rule(FormattingSingleLineConditionalBodyRuleId, "An IF/WHILE/ELSE body is a single unbraced statement sharing its own keyword's line - visually easy to misread. Purely a readability signal - no query result or plan is affected."),
            Rule(FormattingDanglingStatementAfterUnbracedBodyRuleId, "A statement immediately follows an unbraced IF/WHILE's single-statement body, visually appearing to still be inside the conditional/loop when it is not. The statement's own behavior is unaffected - only a future edit relying on the misleading visual shape is at risk."),
            Rule(FormattingIfImmediatelyFollowingPriorBlockEndRuleId, "An IF immediately follows the closing END of a prior braced IF on the same line - easy to misread as an ELSE IF continuation when it is really a separate, unconditional statement. The statement's own behavior is unaffected - only a future edit relying on the misleading visual shape is at risk."),
            Rule(FormattingRedundantParenthesesRuleId, "A parenthesized expression whose parentheses do not change grouping or precedence at all. Purely a readability signal - no query result or plan is affected."),
            Rule(FormattingMissingFileHeaderCommentRuleId, "A module's own definition does not begin with a comment before its first real statement. Purely advisory - T-SQL modules carry no universal file-header convention the way application source files do."),
            Rule(NamingReservedKeywordAsIdentifierRuleId, "A declared identifier is spelled identically to a reserved T-SQL keyword, forcing every future reference to remember to bracket- or quote-delimit it."),
            Rule(NamingSpPrefixOnUserRoutineRuleId, "A user-defined procedure or function is named with the \"sp_\" prefix, reserved by convention for system-shipped procedures - SQL Server searches the master database first for any unqualified call, adding lookup overhead and risking a silent collision with a real system procedure of the same name."),
            Rule(NamingUnqualifiedCreateRuleId, "A CREATE/ALTER for a schema-scoped procedure/function/view names it with no explicit schema qualifier - the object's real owning schema then depends on the connecting principal's own default schema at deployment time."),
            Rule(NamingRedundantTypeQualifierRuleId, "A data type reference carries a redundant \"dbo.\" schema qualifier that adds nothing and couples the declaration to a schema it does not need to name."),
            Rule(DeadCodeUnreachableCodeRuleId, "A statement structurally can never execute - it follows a statement that always ends the enclosing routine on every path (RETURN/THROW, or an IF/TRY-CATCH whose every branch itself always ends it)."),
            Rule(DeadCodeUnusedLabelRuleId, "A label target that no GOTO anywhere in the same routine ever jumps to."),
            Rule(DeadCodeUnusedLocalVariableRuleId, "A DECLARE'd local variable is never read anywhere after being declared - only ever assigned, or never referenced at all."),
            Rule(DeadCodeUnusedParameterRuleId, "A non-OUTPUT formal parameter is never referenced anywhere in the routine body."),
            Rule(DeadCodeRedundantJumpRuleId, "A GOTO whose target label is the very next statement in the same straight-line sequence - jumping to exactly where control flow would already go."),
            Rule(DuplicationCommentedOutCodeRuleId, "A comment's own stripped content reparses cleanly as a plausible T-SQL statement or batch - not prose that merely mentions SQL keywords."),
            Rule(DuplicationDuplicatedStringLiteralRuleId, "The same non-trivial string literal appears three or more times within one module - a magic value that should be a variable or constant instead."),
            Rule(DuplicationSingleIterationLoopRuleId, "A WHILE loop's own body unconditionally reaches a BREAK/RETURN/THROW on every path through the first iteration - it can never loop a second time."),
            Rule(DuplicationSelfAssignmentRuleId, "A pure no-op assignment: a variable or UPDATE column assigned to itself."),
            Rule(DuplicationIdenticalBinaryOperandsRuleId, "The identical expression appears on both sides of a comparison, AND/OR, or a self-referential arithmetic operator - always the same value, a tautology, or a fixed degenerate result."),
            Rule(DuplicationRepeatedUnaryOperatorRuleId, "The same unary operator (NOT, unary minus, bitwise NOT) is applied twice in a row - always simplifiable to a single application or none."),
            Rule(DuplicationNegatedComparisonAsOppositeRuleId, "A negated comparison is written instead of its provably equivalent opposite operator - a readability suggestion, not a correctness claim."),
            Rule(DuplicationDuplicateSiblingConditionRuleId, "A later branch in an IF/ELSE IF chain or CASE expression repeats an earlier sibling's own condition verbatim - the later branch can never be reached."),
            Rule(DuplicationIdenticalBranchBodiesRuleId, "Two (but not all) branches of a conditional structure have an identical body or result - either the conditional is partly pointless, or a copy-paste mistake left one branch matching another."),
            Rule(DuplicationAllBranchesIdenticalRuleId, "Every branch of a conditional structure, including its ELSE, has an identical body or result - the structure produces the same outcome no matter which branch is taken."),
            Rule(DuplicationRedundantAndConditionRuleId, "Two conjuncts of one AND-combined condition compare the same operand against numeric bounds where one bound's range is already a subset of the other's - the looser bound adds nothing."),
            Rule(DuplicationMutuallyExclusiveAndConditionRuleId, "Two conjuncts of one AND-combined condition compare the same operand against numeric bounds whose ranges cannot both hold at once - the condition can never be true."),
            Rule(DuplicationCollapsibleNestedIfRuleId, "An IF with no ELSE whose entire body is a single nested IF, also with no ELSE - semantically identical to one IF combining both conditions with AND."),
            Rule(DuplicationNestedConditionalExpressionRuleId, "An IIF call is nested inside another IIF call's own THEN or ELSE branch - T-SQL's equivalent of a nested ternary expression."),
            Rule(DuplicationAlwaysTrueOrFalseLiteralComparisonRuleId, "A comparison between two literal values (never a column or variable) is provable at parse time regardless of any row's real data - the predicate is dead weight or can never match."),
            Rule(DeprecatedSyntaxTaskCommentTodoRuleId, "A comment contains an untracked \"TODO\" marker."),
            Rule(DeprecatedSyntaxTaskCommentFixmeRuleId, "A comment contains an untracked \"FIXME\" marker."),
            Rule(DeprecatedSyntaxNonAnsiComparisonOperatorRuleId, "A non-ANSI comparison operator (!=, !<, !>) is used instead of the ANSI-standard spelling."),
            Rule(DeprecatedSyntaxEqualsNullComparisonRuleId, "\"= NULL\" never matches any row under the default ANSI_NULLS ON session setting, including a genuinely NULL value - use \"IS NULL\" instead."),
            Rule(DeprecatedSyntaxNotEqualsNullComparisonRuleId, "\"<> NULL\"/\"!= NULL\" never matches any row under the default ANSI_NULLS ON session setting - use \"IS NOT NULL\" instead."),
            Rule(DeprecatedSyntaxLikeWithNoWildcardRuleId, "A LIKE pattern contains no wildcard character - behaviorally equivalent to a plain \"=\" comparison."),
            Rule(DeprecatedSyntaxLegacySystemCompatibilityViewRuleId, "A reference to a pre-SQL-Server-2005 system compatibility view, retained only for backward compatibility and missing columns/rows the real sys.* catalog view exposes."),
            Rule(DeprecatedSyntaxTableHintWithoutWithRuleId, "A table hint is written without the WITH keyword - a deprecated syntax form still accepted by the parser and engine."),
            Rule(DeprecatedSyntaxNumberedProcedureDefinitionRuleId, "A procedure is defined as a numbered-procedure-group member - a deprecated T-SQL feature still accepted by the parser and engine."),
            Rule(DeprecatedSyntaxNumberedProcedureExecutionRuleId, "A procedure is invoked by its numbered-procedure-group number."),
            Rule(DeprecatedSyntaxStringLiteralColumnAliasRuleId, "A column alias is written as a string literal instead of a real identifier - a deprecated aliasing form still accepted by the parser and engine."),
            Rule(DeprecatedSyntaxRemovedSecurityStoredProcedureRuleId, "A legacy security-administration system stored procedure is invoked, superseded by CREATE LOGIN/CREATE USER/ALTER ROLE - some names in this family are already fully removed from current SQL Server versions."),
            Rule(DeprecatedSyntaxDeprecatedSetRowcountRuleId, "SET ROWCOUNT is deprecated - use TOP (n) instead; Microsoft documents it as not honored by INSERT/UPDATE/DELETE in a future release."),
            Rule(StatementShapeInsertWithoutColumnListRuleId, "An INSERT with no explicit column list silently breaks if the target table's column order/count ever changes."),
            Rule(StatementShapeOrdinalOrderByRuleId, "An ORDER BY references a SELECT-list position by ordinal number - silently wrong if the SELECT list's own column order changes."),
            Rule(StatementShapeTopWithoutOrderByRuleId, "A TOP row-limiting clause has no ORDER BY anywhere in the query - Microsoft's own documentation states which rows come back is not guaranteed in this shape."),
            Rule(StatementShapeTableWithNoPrimaryKeyRuleId, "A base table has no PRIMARY KEY constraint - no engine-enforced row uniqueness."),
            Rule(StatementShapeMissingSetNocountOnRuleId, "A procedure or trigger never sets NOCOUNT ON - every DML statement it runs sends a client-visible rowcount message."),
            Rule(StatementShapeBareSelectStarRuleId, "A bare SELECT * couples the query to the target's current column set."),
        ];

        // Both confidence-suffixed variants are generated for every base rule (except the
        // DynamicSqlOutcome family, which never carries a Confidence field at all) - a rule entry
        // with no possible producer would itself be the kind of silent-until-someone-checks noise
        // CLAUDE.md's "never silently counted as clean" warns against, but the reverse (a real
        // producer whose rule ID is missing from this catalog) is the same class of gap, so both
        // variants are pre-registered unconditionally rather than added reactively per producer.
        var mediumVariants = baseRules
            .Where(rule => !DynamicSqlOutcomeRuleIds.Contains(rule.Id))
            .Select(rule => Rule(RuleId(rule.Id, FindingConfidence.Medium), $"(Medium confidence) {rule.ShortDescription.Text}"));
        var lowVariants = baseRules
            .Where(rule => !DynamicSqlOutcomeRuleIds.Contains(rule.Id))
            .Select(rule => Rule(RuleId(rule.Id, FindingConfidence.Low), $"(Low confidence) {rule.ShortDescription.Text}"));

        return [.. baseRules, .. mediumVariants, .. lowVariants];
    }

    private static SarifRule Rule(string id, string description) => new(id, new SarifMessage(description));
}
