using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class WaitForRule : IPerFileRule
{
    public string Id => "WaitForScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => WaitForScanner.Scan(parseResult);
}

internal sealed class CompositeIndexLeadingColumnRule : IPerFileRule
{
    public string Id => "CompositeIndexLeadingColumnScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CompositeIndexLeadingColumnScanner.Scan(parseResult, context.Catalog);
}

internal sealed class MissingStatisticsRule : IPerFileRule
{
    public string Id => "MissingStatisticsScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => MissingStatisticsScanner.Scan(parseResult, context.Catalog);
}

internal sealed class SessionDateSettingRule : IPerFileRule
{
    public string Id => "SessionDateSettingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SessionDateSettingScanner.Scan(parseResult);
}

internal sealed class CartesianJoinRule : IPerFileRule
{
    public string Id => "CartesianJoinScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CartesianJoinScanner.Scan(parseResult, context.Catalog);
}

internal sealed class TruncateSwallowedRule : IPerFileRule
{
    public string Id => "TruncateSwallowedScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => TruncateSwallowedScanner.Scan(parseResult);
}

internal sealed class CatchAllPredicateRule : IPerFileRule
{
    public string Id => "CatchAllPredicateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CatchAllPredicateScanner.Scan(parseResult, context.Catalog);
}

internal sealed class BareTopNoOrderByRule : IPerFileRule
{
    public string Id => "BareTopNoOrderByScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => BareTopNoOrderByScanner.Scan(parseResult);
}

internal sealed class StringConcatNullRule : IPerFileRule
{
    public string Id => "StringConcatNullScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => StringConcatNullScanner.Scan(parseResult, context.Catalog);
}

internal sealed class AggregateDivisionColumnstoreRule : IPerFileRule
{
    public string Id => "AggregateDivisionColumnstoreScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => AggregateDivisionColumnstoreScanner.Scan(parseResult, context.Catalog);
}

internal sealed class ParameterReassignmentPredicateRule : IPerFileRule
{
    public string Id => "ParameterReassignmentPredicateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ParameterReassignmentPredicateScanner.Scan(parseResult, context.Catalog);
}

internal sealed class NotInNullableSubqueryRule : IPerFileRule
{
    public string Id => "NotInNullableSubqueryScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NotInNullableSubqueryScanner.Scan(parseResult, context.Catalog);
}

internal sealed class NonUniqueUpdateSourceRule : IPerFileRule
{
    public string Id => "NonUniqueUpdateSourceScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NonUniqueUpdateSourceScanner.Scan(parseResult, context.Catalog);
}

internal sealed class FloatEqualityPredicateRule : IPerFileRule
{
    public string Id => "FloatEqualityPredicateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => FloatEqualityPredicateScanner.Scan(parseResult, context.Catalog);
}

internal sealed class FloatOrderDependentAggregateRule : IPerFileRule
{
    public string Id => "FloatOrderDependentAggregateScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => FloatOrderDependentAggregateScanner.Scan(parseResult, context.Catalog);
}

internal sealed class AlwaysEncryptedOrderByRule : IPerFileRule
{
    public string Id => "AlwaysEncryptedOrderByScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => AlwaysEncryptedOrderByScanner.Scan(parseResult, context.Catalog);
}

internal sealed class OperandComparabilityRule : IPerFileRule
{
    public string Id => "OperandComparabilityScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => OperandComparabilityScanner.Scan(parseResult, context.Catalog);
}

internal sealed class IndexCoverageRule : IPerFileRule
{
    public string Id => "IndexCoverageScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => IndexCoverageScanner.Scan(parseResult, context.Catalog);
}

internal sealed class SelfReferencingDmlRule : IPerFileRule
{
    public string Id => "SelfReferencingDmlScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SelfReferencingDmlScanner.Scan(parseResult, context.Catalog, context.ViewExpansionMap);
}

internal sealed class TransactionHygieneRule : IPerFileRule
{
    public string Id => "TransactionHygieneScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => TransactionHygieneScanner.Scan(parseResult);
}
