using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class WaitForRule : IPerFileRule
{
    public string Id => "WaitForScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => WaitForScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => WaitForScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => WaitForScanner.Harvest((WaitForScanner.Rule)moduleRule);
}

internal sealed class BackupOptionConflictRule : IPerFileRule
{
    public string Id => "BackupOptionConflictScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => BackupOptionConflictScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => BackupOptionConflictScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => BackupOptionConflictScanner.Harvest((BackupOptionConflictScanner.Rule)moduleRule);
}

internal sealed class GraphPseudoColumnAssignmentRule : IPerFileRule
{
    public string Id => "GraphPseudoColumnAssignmentScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => GraphPseudoColumnAssignmentScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => GraphPseudoColumnAssignmentScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => GraphPseudoColumnAssignmentScanner.Harvest((GraphPseudoColumnAssignmentScanner.Rule)moduleRule);
}

internal sealed class CursorCloseOnCommitRule : IPerFileRule
{
    public string Id => "CursorCloseOnCommitScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CursorCloseOnCommitScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => CursorCloseOnCommitScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => CursorCloseOnCommitScanner.Harvest((CursorCloseOnCommitScanner.Rule)moduleRule);
}

internal sealed class CompositeIndexLeadingColumnRule : IPerFileRule
{
    public string Id => "CompositeIndexLeadingColumnScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CompositeIndexLeadingColumnScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => CompositeIndexLeadingColumnScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => CompositeIndexLeadingColumnScanner.Harvest((CompositeIndexLeadingColumnScanner.Visitor)moduleRule);
}

internal sealed class MissingStatisticsRule : IPerFileRule
{
    public string Id => "MissingStatisticsScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => MissingStatisticsScanner.Scan(parseResult, context.Catalog);
    public IModuleRule? CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) =>
        context.Catalog.IsAutoCreateStatsOn != false ? null : MissingStatisticsScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => MissingStatisticsScanner.Harvest((MissingStatisticsScanner.Visitor)moduleRule);
}

internal sealed class SessionDateSettingRule : IPerFileRule
{
    public string Id => "SessionDateSettingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SessionDateSettingScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => SessionDateSettingScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => SessionDateSettingScanner.Harvest((SessionDateSettingScanner.Rule)moduleRule);
}

internal sealed class CartesianJoinRule : IPerFileRule
{
    public string Id => "CartesianJoinScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CartesianJoinScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => CartesianJoinScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => CartesianJoinScanner.Harvest((CartesianJoinScanner.Rule)moduleRule);
}

internal sealed class OuterJoinPredicateCollapseRule : IPerFileRule
{
    public string Id => "OuterJoinPredicateCollapseScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => OuterJoinPredicateCollapseScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => OuterJoinPredicateCollapseScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => OuterJoinPredicateCollapseScanner.Harvest((OuterJoinPredicateCollapseScanner.Rule)moduleRule);
}

internal sealed class TruncateSwallowedRule : IPerFileRule
{
    public string Id => "TruncateSwallowedScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => TruncateSwallowedScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => TruncateSwallowedScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => TruncateSwallowedScanner.Harvest((TruncateSwallowedScanner.Rule)moduleRule);
}

internal sealed class CatchAllPredicateRule : IPerFileRule
{
    public string Id => "CatchAllPredicateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CatchAllPredicateScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => CatchAllPredicateScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => CatchAllPredicateScanner.Harvest((CatchAllPredicateScanner.Rule)moduleRule);
}

internal sealed class BareTopNoOrderByRule : IPerFileRule
{
    public string Id => "BareTopNoOrderByScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => BareTopNoOrderByScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => BareTopNoOrderByScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => BareTopNoOrderByScanner.Harvest((BareTopNoOrderByScanner.Rule)moduleRule);
}

internal sealed class StringConcatNullRule : IPerFileRule
{
    public string Id => "StringConcatNullScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => StringConcatNullScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => StringConcatNullScanner.CreateRule(parseResult.SourcePath, context.Catalog, parseResult.Fragment);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => StringConcatNullScanner.Harvest((StringConcatNullScanner.Rule)moduleRule);
}

internal sealed class AggregateDivisionColumnstoreRule : IPerFileRule
{
    public string Id => "AggregateDivisionColumnstoreScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => AggregateDivisionColumnstoreScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => AggregateDivisionColumnstoreScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => AggregateDivisionColumnstoreScanner.Harvest((AggregateDivisionColumnstoreScanner.Rule)moduleRule);
}

internal sealed class ParameterReassignmentPredicateRule : IPerFileRule
{
    public string Id => "ParameterReassignmentPredicateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ParameterReassignmentPredicateScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ParameterReassignmentPredicateScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ParameterReassignmentPredicateScanner.Harvest((ParameterReassignmentPredicateScanner.Rule)moduleRule);
}

internal sealed class NotInNullableSubqueryRule : IPerFileRule
{
    public string Id => "NotInNullableSubqueryScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NotInNullableSubqueryScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => NotInNullableSubqueryScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => NotInNullableSubqueryScanner.Harvest((NotInNullableSubqueryScanner.Rule)moduleRule);
}

internal sealed class NonUniqueUpdateSourceRule : IPerFileRule
{
    public string Id => "NonUniqueUpdateSourceScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NonUniqueUpdateSourceScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => NonUniqueUpdateSourceScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => NonUniqueUpdateSourceScanner.Harvest((NonUniqueUpdateSourceScanner.Rule)moduleRule);
}

internal sealed class CheckConstraintPredicateContradictionRule : IPerFileRule
{
    public string Id => "CheckConstraintPredicateContradictionScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CheckConstraintPredicateContradictionScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => CheckConstraintPredicateContradictionScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => CheckConstraintPredicateContradictionScanner.Harvest((CheckConstraintPredicateContradictionScanner.Rule)moduleRule);
}

internal sealed class GeneratedAlwaysColumnAssignmentRule : IPerFileRule
{
    public string Id => "GeneratedAlwaysColumnAssignmentScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => GeneratedAlwaysColumnAssignmentScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => GeneratedAlwaysColumnAssignmentScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => GeneratedAlwaysColumnAssignmentScanner.Harvest((GeneratedAlwaysColumnAssignmentScanner.Rule)moduleRule);
}

internal sealed class FloatEqualityPredicateRule : IPerFileRule
{
    public string Id => "FloatEqualityPredicateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => FloatEqualityPredicateScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => FloatEqualityPredicateScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => FloatEqualityPredicateScanner.Harvest((FloatEqualityPredicateScanner.Rule)moduleRule);
}

internal sealed class FloatOrderDependentAggregateRule : IPerFileRule
{
    public string Id => "FloatOrderDependentAggregateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => FloatOrderDependentAggregateScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => FloatOrderDependentAggregateScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => FloatOrderDependentAggregateScanner.Harvest((FloatOrderDependentAggregateScanner.Rule)moduleRule);
}

internal sealed class AlwaysEncryptedOrderByRule : IPerFileRule
{
    public string Id => "AlwaysEncryptedOrderByScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => AlwaysEncryptedOrderByScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => AlwaysEncryptedOrderByScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => AlwaysEncryptedOrderByScanner.Harvest((AlwaysEncryptedOrderByScanner.Rule)moduleRule);
}

internal sealed class RestrictedImplicitAssignmentRule : IPerFileRule
{
    public string Id => "RestrictedImplicitAssignmentScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => RestrictedImplicitAssignmentScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => RestrictedImplicitAssignmentScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => RestrictedImplicitAssignmentScanner.Harvest((RestrictedImplicitAssignmentScanner.Rule)moduleRule);
}

internal sealed class RevertCookieTypeMismatchRule : IPerFileRule
{
    public string Id => "RevertCookieTypeMismatchScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => RevertCookieTypeMismatchScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => RevertCookieTypeMismatchScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => RevertCookieTypeMismatchScanner.Harvest((RevertCookieTypeMismatchScanner.Rule)moduleRule);
}

internal sealed class ForXmlExplicitInlineXsdRule : IPerFileRule
{
    public string Id => "ForXmlExplicitInlineXsdScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ForXmlExplicitInlineXsdScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ForXmlExplicitInlineXsdScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ForXmlExplicitInlineXsdScanner.Harvest((ForXmlExplicitInlineXsdScanner.Rule)moduleRule);
}

internal sealed class OperandComparabilityRule : IPerFileRule
{
    public string Id => "OperandComparabilityScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => OperandComparabilityScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => OperandComparabilityScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => OperandComparabilityScanner.Harvest((OperandComparabilityScanner.Rule)moduleRule);
}

internal sealed class UnpivotExactTypeMismatchRule : IPerFileRule
{
    public string Id => "UnpivotExactTypeMismatchScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => UnpivotExactTypeMismatchScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => UnpivotExactTypeMismatchScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => UnpivotExactTypeMismatchScanner.Harvest((UnpivotExactTypeMismatchScanner.Rule)moduleRule);
}

internal sealed class SchemaboundAliasTypeRule : IPerFileRule
{
    public string Id => "SchemaboundAliasTypeScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SchemaboundAliasTypeScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => SchemaboundAliasTypeScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => SchemaboundAliasTypeScanner.Harvest((SchemaboundAliasTypeScanner.Rule)moduleRule);
}

internal sealed class IndexCoverageRule : IPerFileRule
{
    public string Id => "IndexCoverageScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => IndexCoverageScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => IndexCoverageScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => IndexCoverageScanner.Harvest((IndexCoverageScanner.Visitor)moduleRule);
}

internal sealed class SelfReferencingDmlRule : IPerFileRule
{
    public string Id => "SelfReferencingDmlScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SelfReferencingDmlScanner.Scan(parseResult, context.Catalog, context.ViewExpansionMap);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => SelfReferencingDmlScanner.CreateRule(parseResult.SourcePath, context.Catalog, context.ViewExpansionMap);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => SelfReferencingDmlScanner.Harvest((SelfReferencingDmlScanner.Rule)moduleRule);
}

internal sealed class TransactionHygieneRule : IPerFileRule
{
    public string Id => "TransactionHygieneScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => TransactionHygieneScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => TransactionHygieneScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => TransactionHygieneScanner.Harvest((TransactionHygieneScanner.Rule)moduleRule);
}

internal sealed class DynamicDataMaskingRule : IPerFileRule
{
    public string Id => "DynamicDataMaskingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => DynamicDataMaskingScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => DynamicDataMaskingScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => DynamicDataMaskingScanner.Harvest((DynamicDataMaskingScanner.Rule)moduleRule);
}
