using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness.Adapters;

internal sealed class TvfFenceRule : IPerFileRule
{
    public string Id => "TvfFenceScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => TvfFenceScanner.Scan(parseResult, context.Catalog, context.TvfFenceMap);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => TvfFenceScanner.CreateRule(parseResult.SourcePath, context.Catalog, context.TvfFenceMap);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => TvfFenceScanner.Harvest((TvfFenceScanner.Rule)moduleRule);
}

internal sealed class ScalarUdfRule : IPerFileRule
{
    public string Id => "ScalarUdfScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => ScalarUdfScanner.Scan(parseResult, context.Catalog, context.ScalarUdfMap);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => ScalarUdfScanner.CreateRule(parseResult.SourcePath, context.Catalog, context.ScalarUdfMap);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => ScalarUdfScanner.Harvest((ScalarUdfScanner.Rule)moduleRule);
}

internal sealed class SecurityRule : IPerFileRule
{
    public string Id => "SecurityScanner";
    public IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state) => SecurityScanner.Scan(parseResult);
    public IModuleRule CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => SecurityScanner.CreateRule(parseResult.SourcePath);
    public IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => SecurityScanner.Harvest((SecurityScanner.Rule)moduleRule);
}
