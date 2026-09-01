using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class KindThenLocationComparer<TFinding>(Func<TFinding, IComparable> kindSelector) : IComparer<IFinding>
    where TFinding : IFinding
{
    public int Compare(IFinding? x, IFinding? y)
    {
        if (x is null || y is null)
        {
            return DefaultLocationComparer.Instance.Compare(x, y);
        }

        var kindCompare = kindSelector((TFinding)x).CompareTo(kindSelector((TFinding)y));
        return kindCompare != 0 ? kindCompare : DefaultLocationComparer.Instance.Compare(x, y);
    }
}

internal sealed class ModuleCompileFlagRule : IPerFileRule
{
    public string Id => "ModuleCompileFlagScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ModuleCompileFlagScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<ModuleCompileFlagFinding>(f => f.Kind);
}

internal sealed class WindowFrameRule : IPerFileRule
{
    public string Id => "WindowFrameScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => WindowFrameScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<WindowFrameFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => WindowFrameScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => WindowFrameScanner.Harvest((WindowFrameScanner.Rule)moduleRule);
}

internal sealed class WindowFunctionArgumentRule : IPerFileRule
{
    public string Id => "WindowFunctionArgumentScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => WindowFunctionArgumentScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<WindowFunctionArgumentFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => WindowFunctionArgumentScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => WindowFunctionArgumentScanner.Harvest((WindowFunctionArgumentScanner.Rule)moduleRule);
}

internal sealed class StringSplitArgumentRule : IPerFileRule
{
    public string Id => "StringSplitArgumentScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => StringSplitArgumentScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<StringSplitArgumentFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => StringSplitArgumentScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => StringSplitArgumentScanner.Harvest((StringSplitArgumentScanner.Rule)moduleRule);
}

internal sealed class ViewOrderingRule : IPerFileRule
{
    public string Id => "ViewOrderingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ViewOrderingScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<ViewOrderingFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ViewOrderingScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ViewOrderingScanner.Harvest((ViewOrderingScanner.Rule)moduleRule);
}

internal sealed class IndexHintRule : IPerFileRule
{
    public string Id => "IndexHintScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => IndexHintScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<IndexHintFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => IndexHintScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => IndexHintScanner.Harvest((IndexHintScanner.Rule)moduleRule);
}

internal sealed class CodeMetricRule : IPerFileRule
{
    public string Id => "CodeMetricScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CodeMetricScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<CodeMetricFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => CodeMetricScanner.CreateRule(parseResult.SourcePath, CodeMetricThresholds.Default);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => CodeMetricScanner.Harvest(parseResult, CodeMetricThresholds.Default, (CodeMetricScanner.Rule)moduleRule);
}

internal sealed class FormattingRule : IPerFileRule
{
    public string Id => "FormattingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => FormattingScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<FormattingFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => FormattingScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => FormattingScanner.Harvest(parseResult, (FormattingScanner.Rule)moduleRule);
}

internal sealed class NamingRule : IPerFileRule
{
    public string Id => "NamingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NamingScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<NamingFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => NamingScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => NamingScanner.Harvest((NamingScanner.Rule)moduleRule);
}

internal sealed class DeadCodeRule : IPerFileRule
{
    public string Id => "DeadCodeScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => DeadCodeScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<DeadCodeFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => DeadCodeScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => DeadCodeScanner.Harvest((DeadCodeScanner.Rule)moduleRule);
}

internal sealed class DuplicationRule : IPerFileRule
{
    public string Id => "DuplicationScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => DuplicationScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<DuplicationFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => DuplicationScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => DuplicationScanner.Harvest(parseResult, context.Catalog, (DuplicationScanner.Rule)moduleRule);
}

internal sealed class DeprecatedSyntaxRule : IPerFileRule
{
    public string Id => "DeprecatedSyntaxScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => DeprecatedSyntaxScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<DeprecatedSyntaxFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => DeprecatedSyntaxScanner.CreateRule(parseResult, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => DeprecatedSyntaxScanner.Harvest(parseResult, (DeprecatedSyntaxScanner.Rule)moduleRule);
}

internal sealed class ControlFlowRiskRule : IPerFileRule
{
    public string Id => "ControlFlowRiskScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ControlFlowRiskScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<ControlFlowRiskFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ControlFlowRiskScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ControlFlowRiskScanner.Harvest((ControlFlowRiskScanner.Rule)moduleRule);
}

internal sealed class QueryAntiPatternRule : IPerFileRule
{
    public string Id => "QueryAntiPatternScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => QueryAntiPatternScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<QueryAntiPatternFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => QueryAntiPatternScanner.CreateRule(parseResult, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => QueryAntiPatternScanner.Harvest((QueryAntiPatternScanner.Rule)moduleRule);
}

internal sealed class TriggerCorrectnessRule : IPerFileRule
{
    public string Id => "TriggerCorrectnessScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => TriggerCorrectnessScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<TriggerCorrectnessFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => TriggerCorrectnessScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => TriggerCorrectnessScanner.Harvest((TriggerCorrectnessScanner.Rule)moduleRule);
}

internal sealed class ForcedSerialRule : IPerFileRule
{
    public string Id => "ForcedSerialScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ForcedSerialScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<ForcedSerialFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ForcedSerialScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ForcedSerialScanner.Harvest((ForcedSerialScanner.Rule)moduleRule);
}

internal sealed class SetOptionRule : IPerFileRule
{
    public string Id => "SetOptionScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SetOptionScanner.Scan(parseResult, context.Catalog, context.Lineage);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<SetOptionFinding>(f => f.Kind);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => SetOptionScanner.CreateRule();
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => SetOptionScanner.Harvest(parseResult, context.Catalog, context.Lineage, (SetOptionScanner.SetStatementRule)moduleRule);
}
