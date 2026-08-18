using SilentScan.Core.Reporting;

namespace SilentScan.Tests.Reporting;

public sealed class RuleCatalogHtmlWriterTests
{
    [Fact]
    public void Write_EveryCatalogRule_LinksToItsOwnRuleDocSiteUrl()
    {
        var html = RuleCatalogHtmlWriter.Write();

        foreach (var rule in RuleCatalog.BaseRules)
        {
            Assert.Contains($"href=\"{RuleDocSite.RelativePath(rule.Id)}\"", html);
        }
    }

    [Fact]
    public void Write_TaglineCounts_MatchTheCatalog()
    {
        var html = RuleCatalogHtmlWriter.Write();

        var withFixGuidance = RuleCatalog.BaseRules.Count(r => r.FixGuidance is not null);
        var withExamples = RuleCatalog.BaseRules.Count(r => r.Examples.Count > 0);

        Assert.Contains($"{RuleCatalog.BaseRules.Count} rules.", html);
        Assert.Contains($"{withFixGuidance} carry fix guidance", html);
        Assert.Contains($"{withExamples} link a real fixture example", html);
    }
}
