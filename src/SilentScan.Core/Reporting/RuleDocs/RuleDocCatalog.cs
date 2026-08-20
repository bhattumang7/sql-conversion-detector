namespace SilentScan.Core.Reporting.RuleDocs;

/// <summary>
/// The rule-id -&gt; <see cref="RuleDocContent"/> lookup <see cref="RulePageHtmlWriter"/> draws
/// from. Each entry's actual prose lives in its own file/class under a per-family subfolder here
/// (e.g. <c>RuleDocs/Tier1/FunctionWrappedColumn.cs</c>) so it can run as long as the rule
/// genuinely needs and be edited independently of every other rule's content; this file is only
/// the wiring list. A rule with no entry here simply gets no rich content on its page - the short
/// <see cref="RuleCatalog"/> rationale still renders, never a fabricated substitute. Populated
/// family-by-family (docs/detection-checklist.md's "Per-rule pages" item).
/// </summary>
public static class RuleDocCatalog
{
    public static IReadOnlyDictionary<string, RuleDocContent> ByRuleId { get; } = new Dictionary<string, RuleDocContent>(StringComparer.Ordinal)
    {
        // Tier1
        [Tier1.FunctionWrappedColumn.RuleId] = Tier1.FunctionWrappedColumn.Content,
        [Tier1.CastOrConvertOnColumn.RuleId] = Tier1.CastOrConvertOnColumn.Content,
        [Tier1.ColumnArithmetic.RuleId] = Tier1.ColumnArithmetic.Content,
        [Tier1.LeadingWildcardLike.RuleId] = Tier1.LeadingWildcardLike.Content,
        [Tier1.LikePatternNotLiteral.RuleId] = Tier1.LikePatternNotLiteral.Content,
        [Tier1.CaseFoldOnColumn.RuleId] = Tier1.CaseFoldOnColumn.Content,
        [Tier1.DateFunctionOnColumn.RuleId] = Tier1.DateFunctionOnColumn.Content,
        [Tier1.CharindexOrLeftOnColumn.RuleId] = Tier1.CharindexOrLeftOnColumn.Content,

        // Verdict
        [Verdict.ScanForced.RuleId] = Verdict.ScanForced.Content,
        [Verdict.RangeSeek.RuleId] = Verdict.RangeSeek.Content,

        // WriteLoss
        [WriteLoss.UnicodeReplacement.RuleId] = WriteLoss.UnicodeReplacement.Content,
        [WriteLoss.ApproximateTruncation.RuleId] = WriteLoss.ApproximateTruncation.Content,
        [WriteLoss.NumericScaleNarrowing.RuleId] = WriteLoss.NumericScaleNarrowing.Content,
        [WriteLoss.TemporalPrecisionLoss.RuleId] = WriteLoss.TemporalPrecisionLoss.Content,

        // Correctness / DML / Join / Predicate (single-rule families)
        [Correctness.NotInNullableSubquery.RuleId] = Correctness.NotInNullableSubquery.Content,
        [Correctness.NonUniqueUpdateSource.RuleId] = Correctness.NonUniqueUpdateSource.Content,
        [Correctness.TemporalBoundaryPrecision.RuleId] = Correctness.TemporalBoundaryPrecision.Content,
        [Join.PartialCompositeForeignKeyJoin.RuleId] = Join.PartialCompositeForeignKeyJoin.Content,
        [Join.CartesianCommaJoin.RuleId] = Join.CartesianCommaJoin.Content,
        [Join.CartesianCrossJoin.RuleId] = Join.CartesianCrossJoin.Content,
        [Dml.SelfReferencingDml.RuleId] = Dml.SelfReferencingDml.Content,
        [Index.KeyLookupProneIndex.RuleId] = Index.KeyLookupProneIndex.Content,
        [Predicate.StringConcatNull.RuleId] = Predicate.StringConcatNull.Content,

        // Naming
        [Naming.ReservedKeywordAsIdentifier.RuleId] = Naming.ReservedKeywordAsIdentifier.Content,
        [Naming.SpPrefixOnUserRoutine.RuleId] = Naming.SpPrefixOnUserRoutine.Content,
        [Naming.UnqualifiedCreate.RuleId] = Naming.UnqualifiedCreate.Content,
        [Naming.RedundantTypeQualifier.RuleId] = Naming.RedundantTypeQualifier.Content,

        // SessionDate
        [SessionDate.SetDateFormat.RuleId] = SessionDate.SetDateFormat.Content,
        [SessionDate.SetDateFirst.RuleId] = SessionDate.SetDateFirst.Content,

        // Hint
        [Hint.IndexDoesNotExist.RuleId] = Hint.IndexDoesNotExist.Content,
        [Hint.HintedIndexNotSeekable.RuleId] = Hint.HintedIndexNotSeekable.Content,

        // WindowFrame
        [WindowFrame.ExplicitRangeFrame.RuleId] = WindowFrame.ExplicitRangeFrame.Content,
        [WindowFrame.ImplicitDefaultRangeFrame.RuleId] = WindowFrame.ImplicitDefaultRangeFrame.Content,
        [Query.BareTopNoOrderBy.RuleId] = Query.BareTopNoOrderBy.Content,

        // QueryAntiPattern
        [QueryAntiPattern.TableVariableLowCompatEstimate.RuleId] = QueryAntiPattern.TableVariableLowCompatEstimate.Content,
        [QueryAntiPattern.TableVariableStaleEstimateInLoop.RuleId] = QueryAntiPattern.TableVariableStaleEstimateInLoop.Content,
        [QueryAntiPattern.RbarSingleRowLoopDml.RuleId] = QueryAntiPattern.RbarSingleRowLoopDml.Content,
        [QueryAntiPattern.GlobalCursorDeclaration.RuleId] = QueryAntiPattern.GlobalCursorDeclaration.Content,
        [QueryAntiPattern.CountStarVariableExistenceCheck.RuleId] = QueryAntiPattern.CountStarVariableExistenceCheck.Content,
        [QueryAntiPattern.NonAggregateHavingPredicate.RuleId] = QueryAntiPattern.NonAggregateHavingPredicate.Content,
        [QueryAntiPattern.UnionOfProvablyDisjointBranches.RuleId] = QueryAntiPattern.UnionOfProvablyDisjointBranches.Content,
        [QueryAntiPattern.DistinctMaskingJoinFanout.RuleId] = QueryAntiPattern.DistinctMaskingJoinFanout.Content,
        [QueryAntiPattern.UnqualifiedTableReference.RuleId] = QueryAntiPattern.UnqualifiedTableReference.Content,
        [QueryAntiPattern.MergeMissingHoldlock.RuleId] = QueryAntiPattern.MergeMissingHoldlock.Content,
        [QueryAntiPattern.MergeNonUniqueUsingSource.RuleId] = QueryAntiPattern.MergeNonUniqueUsingSource.Content,
        [QueryAntiPattern.MergeUnconditionalDelete.RuleId] = QueryAntiPattern.MergeUnconditionalDelete.Content,
        [QueryAntiPattern.RecursiveCteMissingMaxRecursion.RuleId] = QueryAntiPattern.RecursiveCteMissingMaxRecursion.Content,
        [QueryAntiPattern.UnboundedTableWrite.RuleId] = QueryAntiPattern.UnboundedTableWrite.Content,
        [QueryAntiPattern.LinkedServerOrCrossDatabaseReference.RuleId] = QueryAntiPattern.LinkedServerOrCrossDatabaseReference.Content,

        // TriggerCorrectness / ForcedSerial / CrossModule / Trigger
        [TriggerCorrectness.MultiRowUnsafeSingleRowAssignment.RuleId] = TriggerCorrectness.MultiRowUnsafeSingleRowAssignment.Content,
        [TriggerCorrectness.MultiRowUnsafeKeyedDml.RuleId] = TriggerCorrectness.MultiRowUnsafeKeyedDml.Content,
        [TriggerCorrectness.NoEarlyOutForEmptyInvocation.RuleId] = TriggerCorrectness.NoEarlyOutForEmptyInvocation.Content,
        [TriggerCorrectness.DirectRecursiveTrigger.RuleId] = TriggerCorrectness.DirectRecursiveTrigger.Content,
        [TriggerCorrectness.InsteadOfInsertFilteredNoRejectPath.RuleId] = TriggerCorrectness.InsteadOfInsertFilteredNoRejectPath.Content,
        [TriggerCorrectness.UpdateFunctionWithoutValueComparison.RuleId] = TriggerCorrectness.UpdateFunctionWithoutValueComparison.Content,
        [TriggerCorrectness.LogonTriggerHostNameGate.RuleId] = TriggerCorrectness.LogonTriggerHostNameGate.Content,
        [ForcedSerial.TableVariableModification.RuleId] = ForcedSerial.TableVariableModification.Content,
        [ForcedSerial.FastForwardCursor.RuleId] = ForcedSerial.FastForwardCursor.Content,
        [ForcedSerial.NonParallelizableIntrinsic.RuleId] = ForcedSerial.NonParallelizableIntrinsic.Content,
        [CrossModule.InconsistentLockOrder.RuleId] = CrossModule.InconsistentLockOrder.Content,
        [Trigger.MultiHopRecursionCycle.RuleId] = Trigger.MultiHopRecursionCycle.Content,

        // TvfFence / ScalarUdf / Catalog-drift / CallGraph / Predicates (multi-rule families)
        [TvfFence.CorrelatedApply.RuleId] = TvfFence.CorrelatedApply.Content,
        [TvfFence.NestedUnderViewOrTvf.RuleId] = TvfFence.NestedUnderViewOrTvf.Content,
        [TvfFence.FromOrJoin.RuleId] = TvfFence.FromOrJoin.Content,
        [TvfFence.InsertExec.RuleId] = TvfFence.InsertExec.Content,
        [TvfFence.Standalone.RuleId] = TvfFence.Standalone.Content,
        [ScalarUdf.PredicateInvocation.RuleId] = ScalarUdf.PredicateInvocation.Content,
        [ScalarUdf.NestedUnderViewOrTvf.RuleId] = ScalarUdf.NestedUnderViewOrTvf.Content,
        [ScalarUdf.SchemaDependency.RuleId] = ScalarUdf.SchemaDependency.Content,
        [ScalarUdf.ProjectionInvocation.RuleId] = ScalarUdf.ProjectionInvocation.Content,
        [Catalog.ColumnCollationDrift.RuleId] = Catalog.ColumnCollationDrift.Content,
        [Catalog.CrossTableFkTypeDrift.RuleId] = Catalog.CrossTableFkTypeDrift.Content,
        [CallGraph.ArgumentTypeMismatch.RuleId] = CallGraph.ArgumentTypeMismatch.Content,
        [Catalog.MaxTypedColumn.RuleId] = Catalog.MaxTypedColumn.Content,
        [Catalog.RecompilesEveryCall.RuleId] = Catalog.RecompilesEveryCall.Content,
        [Catalog.TableValuedFunctionReturnUsesDatabaseCollation.RuleId] = Catalog.TableValuedFunctionReturnUsesDatabaseCollation.Content,
        [Catalog.DanglingObjectReference.RuleId] = Catalog.DanglingObjectReference.Content,
        [Predicates.FloatEquality.RuleId] = Predicates.FloatEquality.Content,

        // Catalog constraints / Predicates estimate family
        [Catalog.UntrustedForeignKey.RuleId] = Catalog.UntrustedForeignKey.Content,
        [Catalog.UntrustedCheckConstraint.RuleId] = Catalog.UntrustedCheckConstraint.Content,
        [Catalog.CheckConstraintNullNotHandled.RuleId] = Catalog.CheckConstraintNullNotHandled.Content,
        [Catalog.CheckConstraintOnIdentityColumn.RuleId] = Catalog.CheckConstraintOnIdentityColumn.Content,
        [Catalog.DefaultConstraintOnNullableColumn.RuleId] = Catalog.DefaultConstraintOnNullableColumn.Content,
        [Predicates.CatchAllParameter.RuleId] = Predicates.CatchAllParameter.Content,
        [Predicates.LocalVariablePredicate.RuleId] = Predicates.LocalVariablePredicate.Content,
        [Predicates.FilteredIndexParameterMismatch.RuleId] = Predicates.FilteredIndexParameterMismatch.Content,
        [Predicates.ReassignedParameter.RuleId] = Predicates.ReassignedParameter.Content,
        [Predicates.OversizedParameter.RuleId] = Predicates.OversizedParameter.Content,
        [Predicates.UnderLengthParameter.RuleId] = Predicates.UnderLengthParameter.Content,
        [Predicates.AnsiPaddingMismatch.RuleId] = Predicates.AnsiPaddingMismatch.Content,

        // View
        [View.TopPercentOrderByNeverLimits.RuleId] = View.TopPercentOrderByNeverLimits.Content,
        [View.OrderByNotGuaranteedToConsumer.RuleId] = View.OrderByNotGuaranteedToConsumer.Content,

        // TempTable
        [TempTable.UnindexedJoinOperand.RuleId] = TempTable.UnindexedJoinOperand.Content,
        [TempTable.UnindexedWhereFilter.RuleId] = TempTable.UnindexedWhereFilter.Content,

        // Identity
        [Identity.RangeNearExhaustion.RuleId] = Identity.RangeNearExhaustion.Content,
        [Identity.SeedOrIncrementAnomaly.RuleId] = Identity.SeedOrIncrementAnomaly.Content,

        // Declaration
        [Declaration.UndersizedColumn.RuleId] = Declaration.UndersizedColumn.Content,
        [Declaration.UndersizedVariableOrParameter.RuleId] = Declaration.UndersizedVariableOrParameter.Content,

        // Security
        [Security.HardCodedCredential.RuleId] = Security.HardCodedCredential.Content,
        [Security.HardCodedIpAddress.RuleId] = Security.HardCodedIpAddress.Content,
        [Security.WeakHashAlgorithm.RuleId] = Security.WeakHashAlgorithm.Content,
        [Security.WeakHashAlgorithmInSensitiveContext.RuleId] = Security.WeakHashAlgorithmInSensitiveContext.Content,
        [Security.UnprovableDynamicSqlText.RuleId] = Security.UnprovableDynamicSqlText.Content,

        // ControlFlow
        [ControlFlow.CursorFetchColumnCountMismatch.RuleId] = ControlFlow.CursorFetchColumnCountMismatch.Content,
        [ControlFlow.EmptyCatchBlock.RuleId] = ControlFlow.EmptyCatchBlock.Content,
        [ControlFlow.TriggerEmitsOutput.RuleId] = ControlFlow.TriggerEmitsOutput.Content,
        [ControlFlow.DirtyReadIsolationHint.RuleId] = ControlFlow.DirtyReadIsolationHint.Content,
        [ControlFlow.DuplicatedCallArgument.RuleId] = ControlFlow.DuplicatedCallArgument.Content,
        [ControlFlow.LegacyIdentityIntrinsic.RuleId] = ControlFlow.LegacyIdentityIntrinsic.Content,
        [ControlFlow.GotoUsage.RuleId] = ControlFlow.GotoUsage.Content,
        [ControlFlow.CaseExpressionMissingElse.RuleId] = ControlFlow.CaseExpressionMissingElse.Content,
        [ControlFlow.NonDeterministicCaseInput.RuleId] = ControlFlow.NonDeterministicCaseInput.Content,
        [ControlFlow.WaitFor.RuleId] = ControlFlow.WaitFor.Content,
        [ControlFlow.TransactionHygiene.RuleId] = ControlFlow.TransactionHygiene.Content,
        [ControlFlow.TruncateSwallowed.RuleId] = ControlFlow.TruncateSwallowed.Content,
        [ControlFlow.OutputParameter.RuleId] = ControlFlow.OutputParameter.Content,

        // StatementShape
        [StatementShape.InsertWithoutColumnList.RuleId] = StatementShape.InsertWithoutColumnList.Content,
        [StatementShape.OrdinalOrderBy.RuleId] = StatementShape.OrdinalOrderBy.Content,
        [StatementShape.TopWithoutOrderBy.RuleId] = StatementShape.TopWithoutOrderBy.Content,
        [StatementShape.TableWithNoPrimaryKey.RuleId] = StatementShape.TableWithNoPrimaryKey.Content,
        [StatementShape.MissingSetNocountOn.RuleId] = StatementShape.MissingSetNocountOn.Content,
        [StatementShape.BareSelectStar.RuleId] = StatementShape.BareSelectStar.Content,

        // Database
        [Database.PageVerifyNotChecksum.RuleId] = Database.PageVerifyNotChecksum.Content,
        [Database.AutoShrinkOn.RuleId] = Database.AutoShrinkOn.Content,
        [Database.AutoCloseOn.RuleId] = Database.AutoCloseOn.Content,
        [Database.TargetRecoveryTimeUnset.RuleId] = Database.TargetRecoveryTimeUnset.Content,
        [Database.QueryStoreNotReadWrite.RuleId] = Database.QueryStoreNotReadWrite.Content,
        [Database.QueryStoreCaptureModeNotAuto.RuleId] = Database.QueryStoreCaptureModeNotAuto.Content,
        [Database.AutoCreateStatisticsOff.RuleId] = Database.AutoCreateStatisticsOff.Content,
        [Database.AutoUpdateStatisticsOff.RuleId] = Database.AutoUpdateStatisticsOff.Content,
        [Database.CompatibilityLevelBehindEngineDefault.RuleId] = Database.CompatibilityLevelBehindEngineDefault.Content,

        // Lineage
        [Lineage.ExpressionDerivedColumn.RuleId] = Lineage.ExpressionDerivedColumn.Content,
        [Lineage.MultiReferencedCte.RuleId] = Lineage.MultiReferencedCte.Content,
        [Lineage.NestedViewDepth.RuleId] = Lineage.NestedViewDepth.Content,
        [Lineage.PostExpansionJoinWidth.RuleId] = Lineage.PostExpansionJoinWidth.Content,
        [Lineage.SelectStarView.RuleId] = Lineage.SelectStarView.Content,

        // DynamicSql
        [DynamicSql.Analyzed.RuleId] = DynamicSql.Analyzed.Content,
        [DynamicSql.Unanalyzable.RuleId] = DynamicSql.Unanalyzable.Content,
        [DynamicSql.InnerParseFailed.RuleId] = DynamicSql.InnerParseFailed.Content,
        [DynamicSql.PartiallyAnalyzed.RuleId] = DynamicSql.PartiallyAnalyzed.Content,
        [DynamicSql.ConcatenatedValueInConstantSql.RuleId] = DynamicSql.ConcatenatedValueInConstantSql.Content,
        [DynamicSql.ExecStringConcatenatesParameterizableValue.RuleId] = DynamicSql.ExecStringConcatenatesParameterizableValue.Content,
        [DynamicSql.TempTableExecShapeColumnCountMismatch.RuleId] = DynamicSql.TempTableExecShapeColumnCountMismatch.Content,
        [DynamicSql.TempTableExecShapeColumnTypeMismatch.RuleId] = DynamicSql.TempTableExecShapeColumnTypeMismatch.Content,

        // Rest of catalog / predicate singles
        [Catalog.CascadingForeignKey.RuleId] = Catalog.CascadingForeignKey.Content,
        [Catalog.NonPersistedComputedColumn.RuleId] = Catalog.NonPersistedComputedColumn.Content,
        [Catalog.TemporalTableHistoryIndexGap.RuleId] = Catalog.TemporalTableHistoryIndexGap.Content,
        [Catalog.StaleSelectStarView.RuleId] = Catalog.StaleSelectStarView.Content,
        [Catalog.SecurityPredicateIndex.RuleId] = Catalog.SecurityPredicateIndex.Content,
        [Predicate.TryCastComputedColumn.RuleId] = Predicate.TryCastComputedColumn.Content,
        [Predicate.AggregateDivisionColumnstore.RuleId] = Predicate.AggregateDivisionColumnstore.Content,

        // IndexDesign (batch 1 of 2)
        [IndexDesign.HeapWithNonclusteredIndexes.RuleId] = IndexDesign.HeapWithNonclusteredIndexes.Content,
        [IndexDesign.HeapWithNonclusteredPrimaryKey.RuleId] = IndexDesign.HeapWithNonclusteredPrimaryKey.Content,
        [IndexDesign.NonUniqueClusteredIndex.RuleId] = IndexDesign.NonUniqueClusteredIndex.Content,
        [IndexDesign.WideClusteredKey.RuleId] = IndexDesign.WideClusteredKey.Content,
        [IndexDesign.RandomClusteredKeyGuidDefault.RuleId] = IndexDesign.RandomClusteredKeyGuidDefault.Content,
        [IndexDesign.DuplicateIndex.RuleId] = IndexDesign.DuplicateIndex.Content,
        [IndexDesign.SubsumedIndex.RuleId] = IndexDesign.SubsumedIndex.Content,
        [IndexDesign.UnindexedForeignKey.RuleId] = IndexDesign.UnindexedForeignKey.Content,
        [IndexDesign.DisabledIndex.RuleId] = IndexDesign.DisabledIndex.Content,
        [IndexDesign.HypotheticalIndex.RuleId] = IndexDesign.HypotheticalIndex.Content,
        [IndexDesign.ManyNonclusteredIndexes.RuleId] = IndexDesign.ManyNonclusteredIndexes.Content,
        [IndexDesign.ManyKeyColumnsIndex.RuleId] = IndexDesign.ManyKeyColumnsIndex.Content,
        [IndexDesign.WideTable.RuleId] = IndexDesign.WideTable.Content,
        [IndexDesign.HighNullableColumnRatio.RuleId] = IndexDesign.HighNullableColumnRatio.Content,
        [IndexDesign.HighStringColumnRatio.RuleId] = IndexDesign.HighStringColumnRatio.Content,
        [IndexDesign.FilterColumnNotInIndex.RuleId] = IndexDesign.FilterColumnNotInIndex.Content,
        [IndexDesign.DeprecatedLobColumnType.RuleId] = IndexDesign.DeprecatedLobColumnType.Content,
        [IndexDesign.TimestampColumnNaming.RuleId] = IndexDesign.TimestampColumnNaming.Content,
        [IndexDesign.FloatOrRealIndexKeyColumn.RuleId] = IndexDesign.FloatOrRealIndexKeyColumn.Content,
        [IndexDesign.NoRecomputeStatistics.RuleId] = IndexDesign.NoRecomputeStatistics.Content,
        [IndexDesign.VariableLengthKeyColumnExceedsKeyLimit.RuleId] = IndexDesign.VariableLengthKeyColumnExceedsKeyLimit.Content,
        [IndexDesign.MergeableIndexesDifferingIncludeOnly.RuleId] = IndexDesign.MergeableIndexesDifferingIncludeOnly.Content,
        [IndexDesign.ColumnstoreIndexOnDmlTargetTable.RuleId] = IndexDesign.ColumnstoreIndexOnDmlTargetTable.Content,
        [IndexDesign.MonotonicClusteredKeyMissingSequentialOptimization.RuleId] = IndexDesign.MonotonicClusteredKeyMissingSequentialOptimization.Content,
    };
}
