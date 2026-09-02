using SilentScan.Core.Predicates;
namespace SilentScan.Core.Rules;

public static class FindingRuleIds
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
    public const string WriteLossLengthTruncationRuleId = "silentscan/write-loss/length-truncation";
    public const string WriteLossTemporalScaleNarrowingRuleId = "silentscan/write-loss/temporal-scale-narrowing";
    public const string WriteLossTemporalOffsetDroppedRuleId = "silentscan/write-loss/temporal-offset-dropped";
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
    public const string ColumnAnsiPaddingOffRuleId = "silentscan/catalog/column-ansi-padding-off";
    public const string CrossTableTypeDriftRuleId = "silentscan/catalog/cross-table-fk-type-drift";
    public const string ProcCallArgumentMismatchRuleId = "silentscan/call-graph/argument-type-mismatch";
    public const string TvfCallArgumentMismatchRuleId = "silentscan/call-graph/tvf-argument-type-mismatch";
    public const string ProcCallTableValuedArgumentMismatchRuleId = "silentscan/call-graph/table-valued-argument-column-mismatch";
    public const string SpExecuteSqlParameterMismatchRuleId = "silentscan/call-graph/sp-executesql-parameter-type-mismatch";
    public const string TemporalBoundaryPrecisionRuleId = "silentscan/correctness/between-end-of-period-boundary";
    public static string MaxTypedColumnRuleId(NonIndexableColumnFindingKind kind) => kind switch
    {
        NonIndexableColumnFindingKind.MaxLength => "silentscan/catalog/max-typed-column",
        NonIndexableColumnFindingKind.LegacyLargeObject => "silentscan/catalog/legacy-large-object-column",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled NonIndexableColumnFindingKind."),
    };
    public const string ColumnstoreUnsupportedColumnTypeRuleId = "silentscan/catalog/columnstore-unsupported-column-type";
    public static string SelectiveXmlIndexValueColumnRuleId(SelectiveXmlIndexValueColumnFindingKind kind) => kind switch
    {
        SelectiveXmlIndexValueColumnFindingKind.TooWide => "silentscan/catalog/selective-xml-index-value-column-too-wide",
        SelectiveXmlIndexValueColumnFindingKind.LargeObject => "silentscan/catalog/selective-xml-index-value-column-large-object",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled SelectiveXmlIndexValueColumnFindingKind."),
    };
    public static string DynamicDataMaskingRuleId(DynamicDataMaskingFindingKind kind) => kind switch
    {
        DynamicDataMaskingFindingKind.PredicateExposure => "silentscan/predicates/dynamic-data-masking-predicate-exposure",
        DynamicDataMaskingFindingKind.ComputedExpressionCollapse => "silentscan/predicates/dynamic-data-masking-computed-expression-collapse",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled DynamicDataMaskingFindingKind."),
    };
    public const string FloatEqualityRuleId = "silentscan/predicates/float-equality";
    public const string FloatOrderDependentAggregateRuleId = "silentscan/predicates/float-order-dependent-aggregate";
    public const string AlwaysEncryptedOrderByRuleId = "silentscan/predicates/always-encrypted-order-by";
    public const string RestrictedImplicitAssignmentRuleId = "silentscan/predicates/restricted-implicit-assignment";
    public const string RevertCookieTypeMismatchRuleId = "silentscan/predicates/revert-cookie-type-mismatch";
    public const string ForXmlExplicitInlineXsdRuleId = "silentscan/predicates/for-xml-explicit-inline-xsd";
    public const string AlwaysEncryptedKeyColumnRuleId = "silentscan/catalog/always-encrypted-non-enclave-key-column";
    public const string TriggerOrderRuleId = "silentscan/catalog/trigger-firing-order-undefined";
    public static string AlterColumnSafetyRuleId(AlterColumnSafetyKind kind) => kind switch
    {
        AlterColumnSafetyKind.PrecisionOrScaleNarrowing => "silentscan/catalog/alter-column-precision-scale-narrowing",
        AlterColumnSafetyKind.IncompatibleFamilyConversion => "silentscan/catalog/alter-column-incompatible-family-conversion",
        AlterColumnSafetyKind.TemporalOffsetDropped => "silentscan/catalog/alter-column-temporal-offset-dropped",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled AlterColumnSafetyKind."),
    };
    public static string OperandComparabilityRuleId(OperandComparabilityFindingKind kind) => kind switch
    {
        OperandComparabilityFindingKind.Xml => "silentscan/predicates/xml-operand-not-comparable",
        OperandComparabilityFindingKind.LegacyLargeObject => "silentscan/predicates/legacy-lob-operand-not-comparable",
        OperandComparabilityFindingKind.Json => "silentscan/predicates/json-operand-not-comparable",
        OperandComparabilityFindingKind.Spatial => "silentscan/predicates/spatial-operand-not-comparable",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled OperandComparabilityFindingKind."),
    };
    public static string DropProtectedObjectRuleId(DropProtectedObjectKind kind) => kind switch
    {
        DropProtectedObjectKind.SchemaNotEmpty => "silentscan/catalog/drop-schema-not-empty",
        DropProtectedObjectKind.FixedDatabaseRole => "silentscan/catalog/drop-fixed-database-role",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled DropProtectedObjectKind."),
    };
    public static string OnlineRebuildLegacyLobRuleId(OnlineRebuildLegacyLobKind kind) => kind switch
    {
        OnlineRebuildLegacyLobKind.AlterTableRebuild => "silentscan/catalog/online-rebuild-legacy-lob-alter-table",
        OnlineRebuildLegacyLobKind.AlterIndexAllRebuild => "silentscan/catalog/online-rebuild-legacy-lob-alter-index-all",
        OnlineRebuildLegacyLobKind.AlterColumnOnline => "silentscan/catalog/online-rebuild-legacy-lob-alter-column",
        OnlineRebuildLegacyLobKind.DropIndexOnline => "silentscan/catalog/online-rebuild-legacy-lob-drop-index",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled OnlineRebuildLegacyLobKind."),
    };
    public const string MemoryOptimizedUnsupportedColumnTypeRuleId = "silentscan/catalog/memory-optimized-unsupported-column-type";
    public const string UnpivotExactTypeMismatchRuleId = "silentscan/catalog/unpivot-exact-type-mismatch";
    public const string SchemaboundAliasTypeRuleId = "silentscan/catalog/schemabound-alias-type";
    public const string SparseColumnDisallowedTypeRuleId = "silentscan/catalog/sparse-column-disallowed-type";
    public const string LegacyLobUtf8CollationRuleId = "silentscan/catalog/legacy-lob-utf8-collation";
    public const string MemoryOptimizedUtf8CollationRuleId = "silentscan/catalog/memory-optimized-utf8-collation";
    public const string NativelyCompiledUnsupportedBuiltinRuleId = "silentscan/predicate/natively-compiled-unsupported-builtin";
    public static string MemoryOptimizedUnsupportedIndexOptionRuleId(MemoryOptimizedUnsupportedIndexOptionKind kind) => kind switch
    {
        MemoryOptimizedUnsupportedIndexOptionKind.ClusteredIndex => "silentscan/catalog/memory-optimized-clustered-index",
        MemoryOptimizedUnsupportedIndexOptionKind.IncludedColumns => "silentscan/catalog/memory-optimized-index-included-columns",
        MemoryOptimizedUnsupportedIndexOptionKind.FilteredIndex => "silentscan/catalog/memory-optimized-filtered-index",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled MemoryOptimizedUnsupportedIndexOptionKind."),
    };
    public static string MemoryOptimizedForeignKeyRuleId(MemoryOptimizedForeignKeyFindingKind kind) => kind switch
    {
        MemoryOptimizedForeignKeyFindingKind.CrossStorageForeignKey => "silentscan/catalog/memory-optimized-cross-storage-foreign-key",
        MemoryOptimizedForeignKeyFindingKind.ReferentialAction => "silentscan/catalog/memory-optimized-foreign-key-referential-action",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled MemoryOptimizedForeignKeyFindingKind."),
    };
    public const string MemoryOptimizedSchemaOnlyDurabilityRuleId = "silentscan/catalog/memory-optimized-schema-only-durability";
    public const string QueryAntiPatternTableVariableLowCompatEstimateRuleId = "silentscan/query/table-variable-low-compat-estimate";
    public const string QueryAntiPatternTableVariablePspSkipRuleId = "silentscan/query/table-variable-psp-skip";
    public const string QueryAntiPatternTableVariableStaleEstimateInLoopRuleId = "silentscan/query/table-variable-stale-estimate-in-loop";
    public const string QueryAntiPatternRbarSingleRowLoopDmlRuleId = "silentscan/query/rbar-single-row-loop-dml";
    public const string QueryAntiPatternGlobalCursorDeclarationRuleId = "silentscan/query/global-cursor-declaration";
    public const string QueryAntiPatternCountStarVariableExistenceCheckRuleId = "silentscan/query/count-star-variable-existence-check";
    public const string QueryAntiPatternNonAggregateHavingPredicateRuleId = "silentscan/query/non-aggregate-having-predicate";
    public const string QueryAntiPatternUnionOfProvablyDisjointBranchesRuleId = "silentscan/query/union-of-provably-disjoint-branches";
    public const string QueryAntiPatternDistinctMaskingJoinFanoutRuleId = "silentscan/query/distinct-masking-join-fanout";
    public const string QueryAntiPatternUnqualifiedTableReferenceRuleId = "silentscan/query/unqualified-table-reference";
    public const string QueryAntiPatternMergeMissingHoldlockRuleId = "silentscan/query/merge-missing-holdlock";
    public const string QueryAntiPatternMergeNonUniqueUsingSourceRuleId = "silentscan/query/merge-non-unique-using-source";
    public const string QueryAntiPatternMergeUnconditionalDeleteRuleId = "silentscan/query/merge-unconditional-delete";
    public const string QueryAntiPatternRecursiveCteMissingMaxRecursionRuleId = "silentscan/query/recursive-cte-missing-maxrecursion";
    public const string QueryAntiPatternUnboundedTableWriteRuleId = "silentscan/query/unbounded-table-write";
    public const string QueryAntiPatternLinkedServerOrCrossDatabaseReferenceRuleId = "silentscan/query/linked-server-or-cross-database-reference";
    public const string QueryAntiPatternMultiRowInsertIgnoreDupKeyDropRuleId = "silentscan/query/multi-row-insert-ignore-dup-key-drop";
    public const string QueryAntiPatternAlterTableSwitchColumnMismatchRuleId = "silentscan/query/alter-table-switch-column-mismatch";
    public const string QueryAntiPatternAlterTableSwitchIndexMismatchRuleId = "silentscan/query/alter-table-switch-index-mismatch";
    public const string QueryAntiPatternAlterTableSwitchConstraintMismatchRuleId = "silentscan/query/alter-table-switch-constraint-mismatch";
    public const string QueryAntiPatternAlterTableSwitchTargetOnlyIndexRestrictionRuleId = "silentscan/query/alter-table-switch-target-only-index-restriction";
    public const string QueryAntiPatternAlterTableSwitchFilegroupMismatchRuleId = "silentscan/query/alter-table-switch-filegroup-mismatch";
    public const string QueryAntiPatternAlterTableSwitchTemporalMismatchRuleId = "silentscan/query/alter-table-switch-temporal-mismatch";
    public const string QueryAntiPatternAlterTableSwitchRuleConstraintRuleId = "silentscan/query/alter-table-switch-rule-constraint";
    public const string QueryAntiPatternAlterTableSwitchCdcPartitionSwitchRuleId = "silentscan/query/alter-table-switch-cdc-partition-switch";
    public const string QueryAntiPatternAlterTableSwitchPartitionFilegroupMismatchRuleId = "silentscan/query/alter-table-switch-partition-filegroup-mismatch";
    public const string QueryAntiPatternAlterTableSwitchFullTextIndexRestrictionRuleId = "silentscan/query/alter-table-switch-full-text-index-restriction";
    public const string QueryAntiPatternAlterTableSwitchIndexedViewAlignmentRuleId = "silentscan/query/alter-table-switch-indexed-view-alignment";
    public const string QueryAntiPatternAlterSchemaTransferMsShippedObjectRuleId = "silentscan/query/alter-schema-transfer-ms-shipped-object";
    public const string QueryAntiPatternGroupingSetsCardinalityLimitExceededRuleId = "silentscan/query/grouping-sets-cardinality-limit-exceeded";
    public const string QueryAntiPatternGroupingArgumentNotInGroupByListRuleId = "silentscan/query/grouping-argument-not-in-group-by-list";
    public const string IndexCoverageKeyLookupProneIndexRuleId = "silentscan/index/key-lookup-prone";
    public const string TriggerCorrectnessMultiRowUnsafeSingleRowAssignmentRuleId = "silentscan/trigger/multi-row-unsafe-single-row-assignment";
    public const string TriggerCorrectnessMultiRowUnsafeKeyedDmlRuleId = "silentscan/trigger/multi-row-unsafe-keyed-dml";
    public const string TriggerCorrectnessNoEarlyOutForEmptyInvocationRuleId = "silentscan/trigger/no-early-out-for-empty-invocation";
    public const string TriggerCorrectnessDirectRecursiveTriggerRuleId = "silentscan/trigger/direct-recursive-trigger";
    public const string TriggerCorrectnessInsteadOfInsertFilteredNoRejectPathRuleId = "silentscan/trigger/instead-of-insert-filtered-no-reject-path";
    public const string TriggerCorrectnessUpdateFunctionWithoutValueComparisonRuleId = "silentscan/trigger/update-function-without-value-comparison";
    public const string TriggerCorrectnessLogonTriggerHostNameGateRuleId = "silentscan/trigger/logon-trigger-host-name-gate";
    public const string CrossModuleLockOrderRuleId = "silentscan/cross-module/inconsistent-lock-order";
    public const string TriggerRecursionCycleRuleId = "silentscan/trigger/multi-hop-recursion-cycle";
    public const string OversizedParameterRuleId = "silentscan/predicates/oversized-parameter";
    public const string UnderLengthParameterRuleId = "silentscan/predicates/under-length-parameter";
    public const string AnsiPaddingMismatchRuleId = "silentscan/predicates/ansi-padding-mismatch";
    public const string CatchAllPredicateRuleId = "silentscan/predicates/catch-all-parameter";
    public const string LocalVariablePredicateRuleId = "silentscan/predicates/local-variable-predicate";
    public const string FilteredIndexParameterMismatchRuleId = "silentscan/predicates/filtered-index-parameter-mismatch";
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
    public const string DeprecatedSyntaxLegacyLobStatementRuleId = "silentscan/deprecated-syntax/legacy-lob-statement";
    public const string DeprecatedSyntaxLegacyLobFunctionRuleId = "silentscan/deprecated-syntax/legacy-lob-function";
    public const string StatementShapeInsertWithoutColumnListRuleId = "silentscan/statement-shape/insert-without-column-list";
    public const string StatementShapeOrdinalOrderByRuleId = "silentscan/statement-shape/ordinal-order-by";
    public const string StatementShapeTableWithNoPrimaryKeyRuleId = "silentscan/statement-shape/table-with-no-primary-key";
    public const string StatementShapeMissingSetNocountOnRuleId = "silentscan/statement-shape/missing-set-nocount-on";
    public const string StatementShapeBareSelectStarRuleId = "silentscan/statement-shape/bare-select-star";
    public const string ControlFlowRiskCursorFetchColumnCountMismatchRuleId = "silentscan/control-flow/cursor-fetch-column-count-mismatch";
    public const string ControlFlowRiskEmptyCatchBlockRuleId = "silentscan/control-flow/empty-catch-block";
    public const string ControlFlowRiskTriggerEmitsOutputRuleId = "silentscan/control-flow/trigger-emits-output";
    public const string ControlFlowRiskDirtyReadIsolationHintRuleId = "silentscan/control-flow/dirty-read-isolation-hint";
    public const string ControlFlowRiskReadCommittedLockRevertsRowVersioningRuleId = "silentscan/control-flow/read-committed-lock-reverts-row-versioning";
    public const string ControlFlowRiskDuplicatedCallArgumentRuleId = "silentscan/control-flow/duplicated-call-argument";
    public const string ControlFlowRiskLegacyIdentityIntrinsicRuleId = "silentscan/control-flow/legacy-identity-intrinsic";
    public const string ControlFlowRiskGotoUsageRuleId = "silentscan/control-flow/goto-usage";
    public const string ControlFlowRiskCaseExpressionMissingElseRuleId = "silentscan/control-flow/case-expression-missing-else";
    public const string ControlFlowRiskNonDeterministicCaseInputRuleId = "silentscan/control-flow/non-deterministic-case-input";
    public const string NotInNullableSubqueryRuleId = "silentscan/correctness/not-in-nullable-subquery";
    public const string NonUniqueUpdateSourceRuleId = "silentscan/correctness/nonunique-update-source";
    public const string CheckConstraintPredicateContradictionIntervalRuleId = "silentscan/correctness/check-constraint-predicate-contradiction";
    public const string NotNullPredicateContradictionRuleId = "silentscan/correctness/not-null-predicate-contradiction";
    public const string ViewCheckOptionContradictionRuleId = "silentscan/correctness/view-check-option-contradiction";
    public const string ForcedSerialTableVariableModificationRuleId = "silentscan/forced-serial/table-variable-modification";
    public const string ForcedSerialFastForwardCursorRuleId = "silentscan/forced-serial/fast-forward-cursor";
    public const string ForcedSerialNonParallelizableIntrinsicRuleId = "silentscan/forced-serial/nonparallelizable-intrinsic";
    public const string UntrustedForeignKeyRuleId = "silentscan/catalog/untrusted-foreign-key";
    public const string UntrustedCheckConstraintRuleId = "silentscan/catalog/untrusted-check-constraint";
    public const string CascadingForeignKeyRuleId = "silentscan/catalog/cascading-foreign-key";
    public const string MultiReferencedCteRuleId = "silentscan/lineage/multi-referenced-cte";
    public const string RecursiveCteAnchorTypeMismatchRuleId = "silentscan/lineage/recursive-cte-anchor-type-mismatch";
    public const string NestedViewDepthRuleId = "silentscan/lineage/nested-view-depth";
    public const string PostExpansionJoinWidthRuleId = "silentscan/lineage/post-expansion-join-width";
    public const string SelectStarViewRuleId = "silentscan/lineage/select-star-view";
    public const string PartialCompositeForeignKeyJoinRuleId = "silentscan/join/partial-composite-fk";
    public const string OuterJoinPredicateCollapseRuleId = "silentscan/join/outer-join-predicate-collapse";
    public const string ConcatenatedValueInConstantSqlRuleId = "silentscan/dynamic-sql/concatenated-value-in-constant-sql";
    public const string ExecStringConcatenatesParameterizableValueRuleId = "silentscan/dynamic-sql/exec-string-concatenates-parameterizable-value";
    public const string TempTableExecShapeColumnCountMismatchRuleId = "silentscan/dynamic-sql/insert-exec-temp-table-column-count-mismatch";
    public const string TempTableExecShapeColumnTypeMismatchRuleId = "silentscan/dynamic-sql/insert-exec-temp-table-column-type-mismatch";
    public const string ExecResultSetsShapeColumnCountMismatchRuleId = "silentscan/dynamic-sql/exec-with-result-sets-column-count-mismatch";
    public const string ExecResultSetsShapeColumnTypeMismatchRuleId = "silentscan/dynamic-sql/exec-with-result-sets-column-type-mismatch";
    public const string NonPersistedComputedColumnRuleId = "silentscan/catalog/non-persisted-computed-column";
    public const string SelfReferencingDmlRuleId = "silentscan/dml/self-referencing";
    public const string TemporalTableHistoryIndexGapRuleId = "silentscan/catalog/temporal-history-index-gap";
    public const string GeneratedAlwaysColumnExplicitInsertRuleId = "silentscan/catalog/generated-always-column-explicit-insert";
    public const string GeneratedAlwaysColumnExplicitUpdateRuleId = "silentscan/catalog/generated-always-column-explicit-update";
    public const string CheckConstraintNullNotHandledRuleId = "silentscan/catalog/check-constraint-null-not-handled";
    public const string CheckConstraintOnIdentityColumnRuleId = "silentscan/catalog/check-constraint-on-identity-column";
    public const string DefaultNullableConstraintRuleId = "silentscan/catalog/default-constraint-on-nullable-column";
    public const string TryCastComputedColumnPredicateRuleId = "silentscan/predicate/try-cast-computed-column";
    public const string StaleSelectStarViewRuleId = "silentscan/catalog/stale-select-star-view";
    public const string BareTopNoOrderByRuleId = "silentscan/query/bare-top-no-order-by";
    public const string StringConcatNullRuleId = "silentscan/predicate/plus-operator-null-propagation";
    public const string AggregateDivisionColumnstoreRuleId = "silentscan/predicate/aggregate-division-columnstore-batch-mode";
    public const string SecurityPredicateIndexRuleId = "silentscan/catalog/rls-predicate-unindexed-column";
    public const string DanglingObjectReferenceRuleId = "silentscan/catalog/dangling-object-reference";
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
    public static string WindowFunctionArgumentRuleId(WindowFunctionArgumentFindingKind kind) => kind switch
    {
        WindowFunctionArgumentFindingKind.LagLeadNegativeOffset => "silentscan/window-function/lag-lead-negative-offset",
        WindowFunctionArgumentFindingKind.PercentileOutOfRange => "silentscan/window-function/percentile-out-of-range",
        WindowFunctionArgumentFindingKind.TableSamplePercentOutOfRange => "silentscan/window-function/tablesample-percent-out-of-range",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string StringSplitArgumentRuleId(StringSplitArgumentFindingKind kind) => kind switch
    {
        StringSplitArgumentFindingKind.SeparatorNotSingleCharacter => "silentscan/string-tvf/string-split-separator-length",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string BoundedStringBuiltinTruncationRuleId(BoundedStringBuiltinTruncationFindingKind kind) => kind switch
    {
        BoundedStringBuiltinTruncationFindingKind.ReplicateResultTruncated => "silentscan/string-builtin/replicate-truncated",
        BoundedStringBuiltinTruncationFindingKind.ReplaceResultTruncated => "silentscan/string-builtin/replace-truncated",
        BoundedStringBuiltinTruncationFindingKind.SpaceResultTruncated => "silentscan/string-builtin/space-truncated",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public const string WaitForRuleId = "silentscan/control-flow/waitfor";
    public const string BackupOptionConflictRuleId = "silentscan/backup/differential-copy-only";
    public const string RestoreOptionConflictRuleId = "silentscan/restore/option-conflict";
    public const string CreateDatabaseOptionConflictRuleId = "silentscan/create-database/option-conflict";
    public const string GraphPseudoColumnAssignmentRuleId = "silentscan/graph/pseudo-column-assignment";
    public const string CursorCloseOnCommitRuleId = "silentscan/control-flow/cursor-close-on-commit";
    public static string TransactionHygieneRuleId(TransactionHygieneFindingKind kind) => kind switch
    {
        TransactionHygieneFindingKind.UnresolvedOnSomePath => "silentscan/control-flow/unresolved-transaction",
        TransactionHygieneFindingKind.ImplicitTransactionUnresolvedOnSomePath => "silentscan/control-flow/unresolved-implicit-transaction",
        TransactionHygieneFindingKind.CommitAfterXactAbortDoomsTransaction => "silentscan/control-flow/commit-after-xact-abort-dooms-transaction",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public const string OutputParameterRuleId = "silentscan/control-flow/unassigned-output-parameter";
    public const string CompositeIndexLeadingColumnRuleId = "silentscan/index-shape/composite-leading-column-unconstrained";
    public const string MissingStatisticsRuleId = "silentscan/statistics/no-applicable-statistic-auto-create-disabled";
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
    public const string AmbiguousDateLiteralConversionRuleId = "silentscan/session-date/ambiguous-date-literal-conversion";
    public static string CartesianJoinRuleId(CartesianJoinKind kind) => kind switch
    {
        CartesianJoinKind.CommaJoin => "silentscan/join/cartesian-comma-join",
        CartesianJoinKind.ExplicitCrossJoin => "silentscan/join/cartesian-cross-join",
        CartesianJoinKind.AlwaysFalseInnerJoinPredicate => "silentscan/join/always-false-inner-join-predicate",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
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
        DatabaseConfigurationFindingKind.AutoCreateStatisticsOff => "silentscan/database/auto-create-statistics-off",
        DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff => "silentscan/database/auto-update-statistics-off",
        DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault => "silentscan/database/compatibility-level-behind-engine-default",
        DatabaseConfigurationFindingKind.SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange => "silentscan/database/spatial-persisted-computed-column-compatibility-change",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string UnindexedTempTableUsageRuleId(UnindexedTempTableUsageKind kind) => kind switch
    {
        UnindexedTempTableUsageKind.JoinOperand => "silentscan/temp-table/unindexed-join-operand",
        UnindexedTempTableUsageKind.FilteredInWhere => "silentscan/temp-table/unindexed-where-filter",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string SecurityRuleId(SecurityFindingKind kind) => kind switch
    {
        SecurityFindingKind.HardCodedCredential => "silentscan/security/hard-coded-credential",
        SecurityFindingKind.HardCodedIpAddress => "silentscan/security/hard-coded-ip-address",
        SecurityFindingKind.WeakHashAlgorithm => "silentscan/security/weak-hash-algorithm",
        SecurityFindingKind.WeakHashAlgorithmInSensitiveContext => "silentscan/security/weak-hash-algorithm-sensitive-context",
        SecurityFindingKind.UnprovableDynamicSqlText => "silentscan/security/unprovable-dynamic-sql-text",
        SecurityFindingKind.ExternalRestEndpointCall => "silentscan/security/external-rest-endpoint-call",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string ViewOrderingRuleId(ViewOrderingFindingKind kind) => kind switch
    {
        ViewOrderingFindingKind.TopPercentOrderByNeverLimits => "silentscan/view/top-percent-order-by-no-op",
        ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer => "silentscan/view/order-by-not-guaranteed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string IndexDesignRuleId(IndexDesignFindingKind kind) => kind switch
    {
        IndexDesignFindingKind.HeapWithNonclusteredIndexes => "silentscan/index-design/heap-with-nonclustered-indexes",
        IndexDesignFindingKind.HeapWithNonclusteredPrimaryKey => "silentscan/index-design/heap-with-nonclustered-primary-key",
        IndexDesignFindingKind.NonUniqueClusteredIndex => "silentscan/index-design/non-unique-clustered-index",
        IndexDesignFindingKind.RandomClusteredKeyGuidDefault => "silentscan/index-design/random-clustered-key-guid-default",
        IndexDesignFindingKind.DuplicateIndex => "silentscan/index-design/duplicate-index",
        IndexDesignFindingKind.SubsumedIndex => "silentscan/index-design/subsumed-index",
        IndexDesignFindingKind.UnindexedForeignKey => "silentscan/index-design/unindexed-foreign-key",
        IndexDesignFindingKind.DisabledIndex => "silentscan/index-design/disabled-index",
        IndexDesignFindingKind.HypotheticalIndex => "silentscan/index-design/hypothetical-index",
        IndexDesignFindingKind.FilterColumnNotInIndex => "silentscan/index-design/filter-column-not-in-index",
        IndexDesignFindingKind.DeprecatedLobColumnType => "silentscan/index-design/deprecated-lob-column-type",
        IndexDesignFindingKind.TimestampColumnNaming => "silentscan/index-design/timestamp-column-naming",
        IndexDesignFindingKind.FloatOrRealIndexKeyColumn => "silentscan/index-design/float-or-real-index-key-column",
        IndexDesignFindingKind.NoRecomputeStatistics => "silentscan/index-design/no-recompute-statistics",
        IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit => "silentscan/index-design/variable-length-key-column-exceeds-key-limit",
        IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly => "silentscan/index-design/mergeable-indexes-differing-include-only",
        IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable => "silentscan/index-design/columnstore-index-on-dml-target-table",
        IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization => "silentscan/index-design/monotonic-clustered-key-missing-sequential-optimization",
        IndexDesignFindingKind.NonAlignedPartitionedIndex => "silentscan/index-design/non-aligned-partitioned-index",
        IndexDesignFindingKind.RowOrPageLockingDisabled => "silentscan/index-design/row-or-page-locking-disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string ForcedParameterizationRuleId(ForcedParameterizationFindingKind kind) => kind switch
    {
        ForcedParameterizationFindingKind.LikePatternLiteral => "silentscan/forced-parameterization/like-pattern-literal",
        ForcedParameterizationFindingKind.TopOrPagingLiteral => "silentscan/forced-parameterization/top-or-paging-literal",
        ForcedParameterizationFindingKind.SelectListLiteral => "silentscan/forced-parameterization/select-list-literal",
        ForcedParameterizationFindingKind.HavingLiteral => "silentscan/forced-parameterization/having-literal",
        ForcedParameterizationFindingKind.OrderByExpressionLiteral => "silentscan/forced-parameterization/order-by-expression-literal",
        ForcedParameterizationFindingKind.TableSampleSizeLiteral => "silentscan/forced-parameterization/table-sample-size-literal",
        ForcedParameterizationFindingKind.DmlOutputListLiteral => "silentscan/forced-parameterization/dml-output-list-literal",
        ForcedParameterizationFindingKind.ConvertStyleCodeLiteral => "silentscan/forced-parameterization/convert-style-code-literal",
        ForcedParameterizationFindingKind.ConstantFoldableExpressionLiteral => "silentscan/forced-parameterization/constant-foldable-expression-literal",
        ForcedParameterizationFindingKind.GroupByExpressionLiteral => "silentscan/forced-parameterization/group-by-expression-literal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string IdentityRangeRuleId(IdentityRangeFindingKind kind) => kind switch
    {
        IdentityRangeFindingKind.IdentityRangeNearExhaustion => "silentscan/identity/range-near-exhaustion",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
    public static string SetOptionRuleId(SetOptionFindingKind kind) => kind switch
    {
        SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature => "silentscan/set-option/quoted-identifier-off",
        SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature => "silentscan/set-option/numeric-roundabort-on",
        SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature => "silentscan/set-option/ansi-nulls-off",
        SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature => "silentscan/set-option/ansi-warnings-off",
        SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature => "silentscan/set-option/concat-null-yields-null-off",
        SetOptionFindingKind.AnsiPaddingOffBlocksIndexedFeature => "silentscan/set-option/ansi-padding-off",
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
    public static string ExecResultSetsShapeRuleId(ExecResultSetsShapeFindingKind kind) => kind switch
    {
        ExecResultSetsShapeFindingKind.ColumnCountMismatch => ExecResultSetsShapeColumnCountMismatchRuleId,
        ExecResultSetsShapeFindingKind.ColumnTypeMismatch => ExecResultSetsShapeColumnTypeMismatchRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled ExecResultSetsShapeFindingKind."),
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
        DeprecatedSyntaxFindingKind.LegacyLobStatement => DeprecatedSyntaxLegacyLobStatementRuleId,
        DeprecatedSyntaxFindingKind.LegacyLobFunction => DeprecatedSyntaxLegacyLobFunctionRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled DeprecatedSyntaxFindingKind."),
    };
    public static string StatementShapeRuleId(StatementShapeFindingKind kind) => kind switch
    {
        StatementShapeFindingKind.InsertWithoutColumnList => StatementShapeInsertWithoutColumnListRuleId,
        StatementShapeFindingKind.OrdinalOrderBy => StatementShapeOrdinalOrderByRuleId,
        StatementShapeFindingKind.TableWithNoPrimaryKey => StatementShapeTableWithNoPrimaryKeyRuleId,
        StatementShapeFindingKind.MissingSetNocountOn => StatementShapeMissingSetNocountOnRuleId,
        StatementShapeFindingKind.BareSelectStar => StatementShapeBareSelectStarRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled StatementShapeFindingKind."),
    };
    public static string ControlFlowRiskRuleId(ControlFlowRiskFindingKind kind) => kind switch
    {
        ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch => ControlFlowRiskCursorFetchColumnCountMismatchRuleId,
        ControlFlowRiskFindingKind.EmptyCatchBlock => ControlFlowRiskEmptyCatchBlockRuleId,
        ControlFlowRiskFindingKind.TriggerEmitsOutput => ControlFlowRiskTriggerEmitsOutputRuleId,
        ControlFlowRiskFindingKind.DirtyReadIsolationHint => ControlFlowRiskDirtyReadIsolationHintRuleId,
        ControlFlowRiskFindingKind.ReadCommittedLockRevertsRowVersioning => ControlFlowRiskReadCommittedLockRevertsRowVersioningRuleId,
        ControlFlowRiskFindingKind.DuplicatedCallArgument => ControlFlowRiskDuplicatedCallArgumentRuleId,
        ControlFlowRiskFindingKind.LegacyIdentityIntrinsic => ControlFlowRiskLegacyIdentityIntrinsicRuleId,
        ControlFlowRiskFindingKind.GotoUsage => ControlFlowRiskGotoUsageRuleId,
        ControlFlowRiskFindingKind.CaseExpressionMissingElse => ControlFlowRiskCaseExpressionMissingElseRuleId,
        ControlFlowRiskFindingKind.NonDeterministicCaseInput => ControlFlowRiskNonDeterministicCaseInputRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled ControlFlowRiskFindingKind."),
    };
    public static string QueryAntiPatternRuleId(QueryAntiPatternFindingKind kind) => kind switch
    {
        QueryAntiPatternFindingKind.TableVariablePspSkip => QueryAntiPatternTableVariablePspSkipRuleId,
        QueryAntiPatternFindingKind.TableVariableLowCompatEstimate => QueryAntiPatternTableVariableLowCompatEstimateRuleId,
        QueryAntiPatternFindingKind.TableVariableStaleEstimateInLoop => QueryAntiPatternTableVariableStaleEstimateInLoopRuleId,
        QueryAntiPatternFindingKind.RbarSingleRowLoopDml => QueryAntiPatternRbarSingleRowLoopDmlRuleId,
        QueryAntiPatternFindingKind.GlobalCursorDeclaration => QueryAntiPatternGlobalCursorDeclarationRuleId,
        QueryAntiPatternFindingKind.CountStarVariableExistenceCheck => QueryAntiPatternCountStarVariableExistenceCheckRuleId,
        QueryAntiPatternFindingKind.NonAggregateHavingPredicate => QueryAntiPatternNonAggregateHavingPredicateRuleId,
        QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches => QueryAntiPatternUnionOfProvablyDisjointBranchesRuleId,
        QueryAntiPatternFindingKind.DistinctMaskingJoinFanout => QueryAntiPatternDistinctMaskingJoinFanoutRuleId,
        QueryAntiPatternFindingKind.UnqualifiedTableReference => QueryAntiPatternUnqualifiedTableReferenceRuleId,
        QueryAntiPatternFindingKind.MergeMissingHoldlock => QueryAntiPatternMergeMissingHoldlockRuleId,
        QueryAntiPatternFindingKind.MergeNonUniqueUsingSource => QueryAntiPatternMergeNonUniqueUsingSourceRuleId,
        QueryAntiPatternFindingKind.MergeUnconditionalDelete => QueryAntiPatternMergeUnconditionalDeleteRuleId,
        QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion => QueryAntiPatternRecursiveCteMissingMaxRecursionRuleId,
        QueryAntiPatternFindingKind.UnboundedTableWrite => QueryAntiPatternUnboundedTableWriteRuleId,
        QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference => QueryAntiPatternLinkedServerOrCrossDatabaseReferenceRuleId,
        QueryAntiPatternFindingKind.MultiRowInsertIgnoreDupKeyDrop => QueryAntiPatternMultiRowInsertIgnoreDupKeyDropRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch => QueryAntiPatternAlterTableSwitchColumnMismatchRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchIndexMismatch => QueryAntiPatternAlterTableSwitchIndexMismatchRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchConstraintMismatch => QueryAntiPatternAlterTableSwitchConstraintMismatchRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchTargetOnlyIndexRestriction => QueryAntiPatternAlterTableSwitchTargetOnlyIndexRestrictionRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchFilegroupMismatch => QueryAntiPatternAlterTableSwitchFilegroupMismatchRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchTemporalMismatch => QueryAntiPatternAlterTableSwitchTemporalMismatchRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchRuleConstraint => QueryAntiPatternAlterTableSwitchRuleConstraintRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchCdcPartitionSwitch => QueryAntiPatternAlterTableSwitchCdcPartitionSwitchRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchPartitionFilegroupMismatch => QueryAntiPatternAlterTableSwitchPartitionFilegroupMismatchRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchFullTextIndexRestriction => QueryAntiPatternAlterTableSwitchFullTextIndexRestrictionRuleId,
        QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment => QueryAntiPatternAlterTableSwitchIndexedViewAlignmentRuleId,
        QueryAntiPatternFindingKind.AlterSchemaTransferMsShippedObject => QueryAntiPatternAlterSchemaTransferMsShippedObjectRuleId,
        QueryAntiPatternFindingKind.GroupingSetsCardinalityLimitExceeded => QueryAntiPatternGroupingSetsCardinalityLimitExceededRuleId,
        QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList => QueryAntiPatternGroupingArgumentNotInGroupByListRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled QueryAntiPatternFindingKind."),
    };
    public static string IndexCoverageRuleId(IndexCoverageFindingKind kind) => kind switch
    {
        IndexCoverageFindingKind.KeyLookupProneIndex => IndexCoverageKeyLookupProneIndexRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled IndexCoverageFindingKind."),
    };
    public static string TriggerCorrectnessRuleId(TriggerCorrectnessFindingKind kind) => kind switch
    {
        TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment => TriggerCorrectnessMultiRowUnsafeSingleRowAssignmentRuleId,
        TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml => TriggerCorrectnessMultiRowUnsafeKeyedDmlRuleId,
        TriggerCorrectnessFindingKind.NoEarlyOutForEmptyInvocation => TriggerCorrectnessNoEarlyOutForEmptyInvocationRuleId,
        TriggerCorrectnessFindingKind.DirectRecursiveTrigger => TriggerCorrectnessDirectRecursiveTriggerRuleId,
        TriggerCorrectnessFindingKind.InsteadOfInsertFilteredNoRejectPath => TriggerCorrectnessInsteadOfInsertFilteredNoRejectPathRuleId,
        TriggerCorrectnessFindingKind.UpdateFunctionWithoutValueComparison => TriggerCorrectnessUpdateFunctionWithoutValueComparisonRuleId,
        TriggerCorrectnessFindingKind.LogonTriggerHostNameGate => TriggerCorrectnessLogonTriggerHostNameGateRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled TriggerCorrectnessFindingKind."),
    };
    public static string UntrustedConstraintRuleId(UntrustedConstraintFindingKind kind) => kind switch
    {
        UntrustedConstraintFindingKind.ForeignKey => UntrustedForeignKeyRuleId,
        UntrustedConstraintFindingKind.CheckConstraint => UntrustedCheckConstraintRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled UntrustedConstraintFindingKind."),
    };
    public static string CheckConstraintRuleId(CheckConstraintFindingKind kind) => kind switch
    {
        CheckConstraintFindingKind.NullNotHandled => CheckConstraintNullNotHandledRuleId,
        CheckConstraintFindingKind.ConstraintOnIdentityColumn => CheckConstraintOnIdentityColumnRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled CheckConstraintFindingKind."),
    };
    public static string CheckConstraintPredicateContradictionRuleId(CheckConstraintPredicateContradictionKind kind) => kind switch
    {
        CheckConstraintPredicateContradictionKind.CheckConstraintInterval => CheckConstraintPredicateContradictionIntervalRuleId,
        CheckConstraintPredicateContradictionKind.NotNullConstraint => NotNullPredicateContradictionRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled CheckConstraintPredicateContradictionKind."),
    };
    public static string GeneratedAlwaysColumnAssignmentRuleId(GeneratedAlwaysColumnAssignmentKind kind) => kind switch
    {
        GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue => GeneratedAlwaysColumnExplicitInsertRuleId,
        GeneratedAlwaysColumnAssignmentKind.ExplicitUpdateValue => GeneratedAlwaysColumnExplicitUpdateRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled GeneratedAlwaysColumnAssignmentKind."),
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
        WriteLossKind.LengthTruncation => WriteLossLengthTruncationRuleId,
        WriteLossKind.TemporalScaleNarrowing => WriteLossTemporalScaleNarrowingRuleId,
        WriteLossKind.TemporalOffsetDropped => WriteLossTemporalOffsetDroppedRuleId,
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
}
