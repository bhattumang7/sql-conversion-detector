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
}

internal sealed class WindowFunctionArgumentRule : IPerFileRule
{
    public string Id => "WindowFunctionArgumentScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => WindowFunctionArgumentScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<WindowFunctionArgumentFinding>(f => f.Kind);
}

internal sealed class ViewOrderingRule : IPerFileRule
{
    public string Id => "ViewOrderingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ViewOrderingScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<ViewOrderingFinding>(f => f.Kind);
}

internal sealed class IndexHintRule : IPerFileRule
{
    public string Id => "IndexHintScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => IndexHintScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<IndexHintFinding>(f => f.Kind);
}

internal sealed class CodeMetricRule : IPerFileRule
{
    public string Id => "CodeMetricScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => CodeMetricScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<CodeMetricFinding>(f => f.Kind);
}

internal sealed class FormattingRule : IPerFileRule
{
    public string Id => "FormattingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => FormattingScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<FormattingFinding>(f => f.Kind);
}

internal sealed class NamingRule : IPerFileRule
{
    public string Id => "NamingScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => NamingScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<NamingFinding>(f => f.Kind);
}

internal sealed class DeadCodeRule : IPerFileRule
{
    public string Id => "DeadCodeScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => DeadCodeScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<DeadCodeFinding>(f => f.Kind);
}

internal sealed class DuplicationRule : IPerFileRule
{
    public string Id => "DuplicationScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => DuplicationScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<DuplicationFinding>(f => f.Kind);
}

internal sealed class DeprecatedSyntaxRule : IPerFileRule
{
    public string Id => "DeprecatedSyntaxScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => DeprecatedSyntaxScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<DeprecatedSyntaxFinding>(f => f.Kind);
}

internal sealed class ControlFlowRiskRule : IPerFileRule
{
    public string Id => "ControlFlowRiskScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ControlFlowRiskScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<ControlFlowRiskFinding>(f => f.Kind);
}

internal sealed class QueryAntiPatternRule : IPerFileRule
{
    public string Id => "QueryAntiPatternScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => QueryAntiPatternScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<QueryAntiPatternFinding>(f => f.Kind);
}

internal sealed class TriggerCorrectnessRule : IPerFileRule
{
    public string Id => "TriggerCorrectnessScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => TriggerCorrectnessScanner.Scan(parseResult, context.Catalog);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<TriggerCorrectnessFinding>(f => f.Kind);
}

internal sealed class ForcedSerialRule : IPerFileRule
{
    public string Id => "ForcedSerialScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ForcedSerialScanner.Scan(parseResult);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<ForcedSerialFinding>(f => f.Kind);
}

internal sealed class SetOptionRule : IPerFileRule
{
    public string Id => "SetOptionScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SetOptionScanner.Scan(parseResult, context.Catalog, context.Lineage);
    public IComparer<IFinding>? Comparer => new KindThenLocationComparer<SetOptionFinding>(f => f.Kind);
}
