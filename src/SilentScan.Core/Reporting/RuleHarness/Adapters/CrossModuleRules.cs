using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class CrossModuleLockOrderRule : ICrossModuleRule
{
    public string Id => "CrossModuleLockOrderScanner";
    public IReadOnlyList<IFinding> Scan(IReadOnlyList<SqlParseResult> parseResults, RuleContext context) => CrossModuleLockOrderScanner.Scan(parseResults, context.Catalog);
}

internal sealed class TriggerRecursionCycleRule : ICrossModuleRule
{
    public string Id => "TriggerRecursionCycleScanner";
    public IReadOnlyList<IFinding> Scan(IReadOnlyList<SqlParseResult> parseResults, RuleContext context) => TriggerRecursionCycleScanner.Scan(parseResults, context.Catalog);
}
