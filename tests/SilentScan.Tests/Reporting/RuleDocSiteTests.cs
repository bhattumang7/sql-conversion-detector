using System.Text.RegularExpressions;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// Pins <see cref="RuleDocSite"/>'s slugging contract: every catalog rule maps to a distinct,
/// stably-formatted page, and every SARIF rule (including confidence-suffixed variants) carries
/// a <c>helpUri</c> pointing at that same page. A rule-ID rename that would silently break a
/// published link shows up here as a slug collision or a mismatched URI, not as a 404 someone
/// finds later.
/// </summary>
public sealed partial class RuleDocSiteTests
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [Fact]
    public void Slug_EveryCatalogRule_IsUniqueAndWellFormed()
    {
        var slugs = RuleCatalog.BaseRules.Select(r => RuleDocSite.Slug(r.Id)).ToList();

        foreach (var slug in slugs)
        {
            Assert.Matches(SlugPattern(), slug);
        }

        var distinct = slugs.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(slugs.Count, distinct);
    }

    [Fact]
    public void BaseRuleId_StripsConfidenceSuffixesOnly()
    {
        Assert.Equal("silentscan/tier1/function-wrapped-column", RuleDocSite.BaseRuleId("silentscan/tier1/function-wrapped-column/medium-confidence"));
        Assert.Equal("silentscan/tier1/function-wrapped-column", RuleDocSite.BaseRuleId("silentscan/tier1/function-wrapped-column/low-confidence"));
        Assert.Equal("silentscan/tier1/function-wrapped-column", RuleDocSite.BaseRuleId("silentscan/tier1/function-wrapped-column"));
    }

    [Fact]
    public void Url_ConfidenceVariant_ResolvesToTheSameUrlAsItsBaseRule()
    {
        var baseUrl = RuleDocSite.Url("silentscan/tier1/function-wrapped-column");
        Assert.Equal(baseUrl, RuleDocSite.Url("silentscan/tier1/function-wrapped-column/medium-confidence"));
        Assert.Equal(baseUrl, RuleDocSite.Url("silentscan/tier1/function-wrapped-column/low-confidence"));
    }

    [Fact]
    public void RelativePath_MatchesUrlsOwnSlug()
    {
        Assert.Equal("rules/tier1-function-wrapped-column.html", RuleDocSite.RelativePath("silentscan/tier1/function-wrapped-column"));
        Assert.Equal(
            RuleDocSite.RelativePath("silentscan/tier1/function-wrapped-column"),
            RuleDocSite.RelativePath("silentscan/tier1/function-wrapped-column/medium-confidence"));
    }

    [Fact]
    public void AllRules_EveryEntry_CarriesAHelpUriMatchingRuleDocSite()
    {
        foreach (var rule in SarifRuleCatalog.AllRules)
        {
            Assert.Equal(RuleDocSite.Url(rule.Id), rule.HelpUri);
        }
    }
}
