using System.Text.Json;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Reporting;

public sealed class SarifReportWriterTests
{
    [Fact]
    public void Write_MiniProjectFixture_ProducesValidSarifWithExpectedResultCount()
    {
        var projectDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "mini_project");
        var report = ScanReportBuilder.Build(SqlFileDiscovery.EnumerateSqlFiles(projectDir));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        Assert.Equal("2.1.0", document.RootElement.GetProperty("version").GetString());
        var results = document.RootElement.GetProperty("runs")[0].GetProperty("results");
        var expectedCount = report.Tier1Findings.Count + report.TypedFindings.Count + report.DynamicSqlFindings.Count + report.ExpressionDerivedFindings.Count;
        Assert.Equal(expectedCount, results.GetArrayLength());
        Assert.True(expectedCount > 0);
    }

    [Fact]
    public void Write_ScanForcedFinding_MapsToErrorLevel()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [new TypedPredicateFinding(
                Verdict.ScanForced,
                new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
                new PredicateOperand.Value(null),
                "=",
                "test.sql",
                1,
                1)],
            [],
            []);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/verdict/scan-forced", result.GetProperty("ruleId").GetString());
    }

    [Fact]
    public void Write_ExpressionDerivedFinding_MapsToErrorLevelWithChainInMessage()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [],
            [],
            [new ExpressionDerivedFinding(
                "CustomerIdAgain",
                "test.sql",
                10,
                5,
                [new TransformationSite("vw_outer.sql", 3, "CAST/CONVERT to Int"), new TransformationSite("vw_inner.sql", 2, "CAST/CONVERT to VarChar(20)")],
                [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: true)])]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/lineage/expression-derived-column", result.GetProperty("ruleId").GetString());
        var message = result.GetProperty("message").GetProperty("text").GetString();
        Assert.Contains("dbo.Orders.CustomerId (indexed)", message, StringComparison.Ordinal);
        Assert.Contains("vw_outer.sql:3", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryTier1FindingKind()
    {
        foreach (var kind in Enum.GetValues<SargabilityFindingKind>())
        {
            var ruleId = SarifRuleCatalog.Tier1RuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryVerdict()
    {
        foreach (var verdict in Enum.GetValues<Verdict>())
        {
            var ruleId = SarifRuleCatalog.VerdictRuleId(verdict);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }
}
