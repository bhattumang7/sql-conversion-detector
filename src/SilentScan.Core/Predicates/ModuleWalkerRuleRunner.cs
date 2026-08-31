using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

internal static class ModuleWalkerRuleRunner
{
    public static IReadOnlyList<TRule> Run<TRule>(
        IEnumerable<SqlParseResult> parseResults,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        Func<string, DatabaseCatalog, TRule> createRule)
        where TRule : IModuleRule
    {
        var rules = new List<TRule>();
        foreach (var result in parseResults)
        {
            var rule = createRule(result.SourcePath, catalog);
            var walker = new ModuleWalker(result.SourcePath, catalog, resolvedViews, rules: [rule]);
            result.Fragment.Accept(walker);
            rules.Add(rule);
        }

        return rules;
    }
}
