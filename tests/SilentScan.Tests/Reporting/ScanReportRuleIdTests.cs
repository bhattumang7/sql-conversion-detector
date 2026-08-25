using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

public sealed class ScanReportRuleIdTests
{
    [Fact]
    public void RuleCatalog_HasOneEntryPerBaseRuleWithMatchingHelpUri()
    {
        var report = TestScanReports.Build();

        Assert.Equal(RuleCatalog.BaseRules.Count, report.RuleCatalog.Count);
        Assert.All(report.RuleCatalog, entry => Assert.Equal(RuleDocSite.Url(entry.RuleId), entry.HelpUri));
        Assert.Equal(
            RuleCatalog.BaseRules.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal),
            report.RuleCatalog.Select(e => e.RuleId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Finding_RuleId_MatchesSarifRuleCatalogForAConstantRuleFamily()
    {
        var finding = new FloatEqualityFinding("dbo.T", "Amount", "float", "test.sql", 1, 1);

        Assert.Equal(SarifRuleCatalog.FloatEqualityRuleId, finding.RuleId);
        Assert.Contains(finding.RuleId, RuleCatalog.BaseRules.Select(r => r.Id));
    }

    [Fact]
    public void Finding_RuleId_MatchesSarifRuleCatalogForAPerKindRuleFamily()
    {
        var wrapped = new SargabilityFinding(SargabilityFindingKind.FunctionWrappedColumn, "Col", null, "test.sql", 1, 1);
        var cast = new SargabilityFinding(SargabilityFindingKind.CastOrConvertOnColumn, "Col", null, "test.sql", 1, 1);

        Assert.Equal(SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.FunctionWrappedColumn), wrapped.RuleId);
        Assert.Equal(SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.CastOrConvertOnColumn), cast.RuleId);
        Assert.NotEqual(wrapped.RuleId, cast.RuleId);
    }

    [Fact]
    public void Finding_RuleId_MatchesSarifRuleCatalogForADiscriminatorNamedDifferentlyFromKind()
    {
        var finding = new DynamicSqlFinding("test.sql", 1, 1, DynamicSqlOutcome.PartiallyAnalyzed, "reason");

        Assert.Equal(SarifRuleCatalog.DynamicSqlRuleId(DynamicSqlOutcome.PartiallyAnalyzed), finding.RuleId);
    }
}
