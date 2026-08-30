using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Reporting.RuleHarness;

public interface IRule
{
    string Id { get; }

    bool ApplyConfidenceFilter => true;
}

public interface IPerFileRule : IRule
{
    object? Prepare(RuleContext context) => null;

    IReadOnlyList<IFinding> Scan(SqlParseResult parseResult, RuleContext context, object? state);

    IReadOnlyList<IFinding> ScanCatalogOnce(RuleContext context) => [];

    IComparer<IFinding>? Comparer => null;

    IModuleRule? CreateModuleRule(SqlParseResult parseResult, RuleContext context, object? state) => null;

    IReadOnlyList<IFinding> HarvestFindings(SqlParseResult parseResult, RuleContext context, object? state, IModuleRule moduleRule) => [];
}

public interface ICatalogRule : IRule
{
    IReadOnlyList<IFinding> Scan(RuleContext context);
}

public interface ICrossModuleRule : IRule
{
    IReadOnlyList<IFinding> Scan(IReadOnlyList<SqlParseResult> parseResults, RuleContext context);

    IModuleRule? CreateModuleRule(SqlParseResult parseResult, RuleContext context) => null;

    IReadOnlyList<IFinding> Aggregate(RuleContext context, IReadOnlyList<IModuleRule> moduleRules) => [];
}
