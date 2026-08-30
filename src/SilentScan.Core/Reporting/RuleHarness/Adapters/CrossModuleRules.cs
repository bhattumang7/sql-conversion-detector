using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class CrossModuleLockOrderRule : ICrossModuleRule
{
    public string Id => "CrossModuleLockOrderScanner";
    public IReadOnlyList<IFinding> Scan(IReadOnlyList<SqlParseResult> parseResults, RuleContext context) => CrossModuleLockOrderScanner.Scan(parseResults, context.Catalog);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context) => CrossModuleLockOrderScanner.CreateRule(parseResult.SourcePath, context.Catalog);
    public IReadOnlyList<IFinding> Aggregate(RuleContext context, IReadOnlyList<IModuleRule> moduleRules) =>
        CrossModuleLockOrderScanner.Harvest(context.Catalog, [.. moduleRules.Cast<CrossModuleLockOrderScanner.Rule>()]);
}

internal sealed class TriggerRecursionCycleRule : ICrossModuleRule
{
    public string Id => "TriggerRecursionCycleScanner";
    public IReadOnlyList<IFinding> Scan(IReadOnlyList<SqlParseResult> parseResults, RuleContext context) => TriggerRecursionCycleScanner.Scan(parseResults, context.Catalog);
    public IModuleRule? CreateModuleRule(SqlParseResult parseResult, RuleContext context) =>
        context.Catalog.IsNestedTriggersEnabled == true ? TriggerRecursionCycleScanner.CreateRule(parseResult.SourcePath, context.Catalog) : null;
    public IReadOnlyList<IFinding> Aggregate(RuleContext context, IReadOnlyList<IModuleRule> moduleRules) =>
        TriggerRecursionCycleScanner.Harvest(context.Catalog, [.. moduleRules.Cast<TriggerRecursionCycleScanner.Rule>()]);
}
