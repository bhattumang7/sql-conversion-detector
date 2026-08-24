namespace SilentScan.Core.Reporting;

public sealed record RuleCatalogEntry(string RuleId, string HelpUri);

public static class RuleCatalogEntries
{
    public static IReadOnlyList<RuleCatalogEntry> All { get; } =
        [.. RuleCatalog.BaseRules.Select(rule => new RuleCatalogEntry(rule.Id, RuleDocSite.Url(rule.Id)))];
}
