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

internal sealed class NativelyCompiledUnsupportedBuiltinRule : IPerFileRule
{
    public string Id => "NativelyCompiledUnsupportedBuiltinScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NativelyCompiledUnsupportedBuiltinScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => NativelyCompiledUnsupportedBuiltinScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => NativelyCompiledUnsupportedBuiltinScanner.Harvest((NativelyCompiledUnsupportedBuiltinScanner.Rule)moduleRule);
}

internal sealed class RestoreOptionConflictRule : IPerFileRule
{
    public string Id => "RestoreOptionConflictScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => RestoreOptionConflictScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => RestoreOptionConflictScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => RestoreOptionConflictScanner.Harvest((RestoreOptionConflictScanner.Rule)moduleRule);
}

internal sealed class CreateDatabaseOptionConflictRule : IPerFileRule
{
    public string Id => "CreateDatabaseOptionConflictScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CreateDatabaseOptionConflictScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => CreateDatabaseOptionConflictScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => CreateDatabaseOptionConflictScanner.Harvest((CreateDatabaseOptionConflictScanner.Rule)moduleRule);
}

internal sealed class ViewCheckOptionContradictionRule : IPerFileRule
{
    public string Id => "ViewCheckOptionContradictionScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ViewCheckOptionContradictionScanner.Scan(parseResult, context.Catalog, context.ViewDefinitions);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ViewCheckOptionContradictionScanner.CreateRule(parseResult.SourcePath, context.Catalog, context.ViewDefinitions);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ViewCheckOptionContradictionScanner.Harvest((ViewCheckOptionContradictionScanner.Rule)moduleRule);
}

internal sealed class GraphPseudoColumnAssignmentRule : IPerFileRule
{
    public string Id => "GraphPseudoColumnAssignmentScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => GraphPseudoColumnAssignmentScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => GraphPseudoColumnAssignmentScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => GraphPseudoColumnAssignmentScanner.Harvest((GraphPseudoColumnAssignmentScanner.Rule)moduleRule);
}

internal sealed class LegacyLobConversionTargetRule : IPerFileRule
{
    public string Id => "LegacyLobConversionTargetScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => LegacyLobConversionTargetScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => LegacyLobConversionTargetScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => LegacyLobConversionTargetScanner.Harvest((LegacyLobConversionTargetScanner.Rule)moduleRule);
}

internal sealed class GroupByValidityRule : IPerFileRule
{
    public string Id => "GroupByValidityScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => GroupByValidityScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => GroupByValidityScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => GroupByValidityScanner.Harvest((GroupByValidityScanner.Rule)moduleRule);
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

internal sealed class AmbiguousDateLiteralConversionRule : IPerFileRule
{
    public string Id => "AmbiguousDateLiteralConversionScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => AmbiguousDateLiteralConversionScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => AmbiguousDateLiteralConversionScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => AmbiguousDateLiteralConversionScanner.Harvest((AmbiguousDateLiteralConversionScanner.Rule)moduleRule);
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

internal sealed class TvfCallArgumentMismatchRule : IPerFileRule
{
    public string Id => "TvfCallArgumentMismatchScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => TvfCallArgumentMismatchScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => TvfCallArgumentMismatchScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => TvfCallArgumentMismatchScanner.Harvest((TvfCallArgumentMismatchScanner.Rule)moduleRule);
}

internal sealed class SemanticSearchRule : IPerFileRule
{
    public string Id => "SemanticSearchScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SemanticSearchScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => SemanticSearchScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => SemanticSearchScanner.Harvest((SemanticSearchScanner.Rule)moduleRule);
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

internal sealed class AlwaysEncryptedAssignmentMismatchRule : IPerFileRule
{
    public string Id => "AlwaysEncryptedAssignmentMismatchScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => AlwaysEncryptedAssignmentMismatchScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => AlwaysEncryptedAssignmentMismatchScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => AlwaysEncryptedAssignmentMismatchScanner.Harvest((AlwaysEncryptedAssignmentMismatchScanner.Rule)moduleRule);
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

internal sealed class VectorFunctionArgumentRule : IPerFileRule
{
    public string Id => "VectorFunctionArgumentScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => VectorFunctionArgumentScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => VectorFunctionArgumentScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => VectorFunctionArgumentScanner.Harvest((VectorFunctionArgumentScanner.Rule)moduleRule);
}

internal sealed class SchemaWithRejectedTypeRule : IPerFileRule
{
    public string Id => "SchemaWithRejectedTypeScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SchemaWithRejectedTypeScanner.Scan(parseResult, context.Catalog);
}

internal sealed class ExternalTableUnsupportedColumnTypeRule : IPerFileRule
{
    public string Id => "ExternalTableUnsupportedColumnTypeScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ExternalTableUnsupportedColumnTypeScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ExternalTableUnsupportedColumnTypeScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ExternalTableUnsupportedColumnTypeScanner.Harvest((ExternalTableUnsupportedColumnTypeScanner.Rule)moduleRule);
}

internal sealed class VectorLiteralConversionRule : IPerFileRule
{
    public string Id => "VectorLiteralConversionScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => VectorLiteralConversionScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => VectorLiteralConversionScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => VectorLiteralConversionScanner.Harvest((VectorLiteralConversionScanner.Rule)moduleRule);
}

internal sealed class FullTextPredicateInAggregateRule : IPerFileRule
{
    public string Id => "FullTextPredicateInAggregateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => FullTextPredicateInAggregateScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => FullTextPredicateInAggregateScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => FullTextPredicateInAggregateScanner.Harvest((FullTextPredicateInAggregateScanner.Rule)moduleRule);
}

internal sealed class ChangeTrackingEncryptedPrimaryKeyRule : IPerFileRule
{
    public string Id => "ChangeTrackingEncryptedPrimaryKeyScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ChangeTrackingEncryptedPrimaryKeyScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ChangeTrackingEncryptedPrimaryKeyScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ChangeTrackingEncryptedPrimaryKeyScanner.Harvest((ChangeTrackingEncryptedPrimaryKeyScanner.Rule)moduleRule);
}

internal sealed class XmlSchemaCollectionDisallowedTypeRule : IPerFileRule
{
    public string Id => "XmlSchemaCollectionDisallowedTypeScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => XmlSchemaCollectionDisallowedTypeScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => XmlSchemaCollectionDisallowedTypeScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => XmlSchemaCollectionDisallowedTypeScanner.Harvest((XmlSchemaCollectionDisallowedTypeScanner.Rule)moduleRule);
}

internal sealed class XmlSchemaCollectionMismatchRule : IPerFileRule
{
    public string Id => "XmlSchemaCollectionMismatchScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => XmlSchemaCollectionMismatchScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => XmlSchemaCollectionMismatchScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => XmlSchemaCollectionMismatchScanner.Harvest((XmlSchemaCollectionMismatchScanner.Rule)moduleRule);
}

internal sealed class ExecuteAtLargeObjectParameterRule : IPerFileRule
{
    public string Id => "ExecuteAtLargeObjectParameterScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ExecuteAtLargeObjectParameterScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ExecuteAtLargeObjectParameterScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ExecuteAtLargeObjectParameterScanner.Harvest((ExecuteAtLargeObjectParameterScanner.Rule)moduleRule);
}

internal sealed class UnpivotExactTypeMismatchRule : IPerFileRule
{
    public string Id => "UnpivotExactTypeMismatchScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => UnpivotExactTypeMismatchScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => UnpivotExactTypeMismatchScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => UnpivotExactTypeMismatchScanner.Harvest((UnpivotExactTypeMismatchScanner.Rule)moduleRule);
}

internal sealed class NativelyCompiledClrTypeRule : IPerFileRule
{
    public string Id => "NativelyCompiledClrTypeScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NativelyCompiledClrTypeScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => NativelyCompiledClrTypeScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => NativelyCompiledClrTypeScanner.Harvest((NativelyCompiledClrTypeScanner.Rule)moduleRule);
}

internal sealed class NativelyCompiledErrorOutsideCatchRule : IPerFileRule
{
    public string Id => "NativelyCompiledErrorOutsideCatchScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NativelyCompiledErrorOutsideCatchScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => NativelyCompiledErrorOutsideCatchScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => NativelyCompiledErrorOutsideCatchScanner.Harvest((NativelyCompiledErrorOutsideCatchScanner.Rule)moduleRule);
}

internal sealed class NativelyCompiledInterpretedCalleeRule : IPerFileRule
{
    public string Id => "NativelyCompiledInterpretedCalleeScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NativelyCompiledInterpretedCalleeScanner.Scan(parseResult, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => NativelyCompiledInterpretedCalleeScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => NativelyCompiledInterpretedCalleeScanner.Harvest((NativelyCompiledInterpretedCalleeScanner.Rule)moduleRule);
}

internal sealed class MemoryOptimizedLedgerConflictRule : IPerFileRule
{
    public string Id => "MemoryOptimizedLedgerConflictScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => MemoryOptimizedLedgerConflictScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => MemoryOptimizedLedgerConflictScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => MemoryOptimizedLedgerConflictScanner.Harvest((MemoryOptimizedLedgerConflictScanner.Rule)moduleRule);
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
