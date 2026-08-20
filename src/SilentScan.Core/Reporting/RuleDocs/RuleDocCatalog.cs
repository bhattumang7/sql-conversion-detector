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
    };
}
