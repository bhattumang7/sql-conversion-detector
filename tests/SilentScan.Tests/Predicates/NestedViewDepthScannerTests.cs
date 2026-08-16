using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Nested-view depth report". Catalog/lineage-only, no oracle needed (depth is a pure catalog/AST fact, not a plan-shape claim).</summary>
public sealed class NestedViewDepthScannerTests
{
    private static IReadOnlyList<NestedViewDepthFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var (views, _) = ViewDefinitionExtractor.Extract([result], catalog.DefaultCollation, catalog.TypeAliases, ledger: null);
        var expansionMap = ViewExpansionMap.Build(views, catalog);
        return NestedViewDepthScanner.Scan(expansionMap, views);
    }

    [Fact]
    public void ViewOverBaseTable_Depth0_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.v1 AS SELECT Id FROM dbo.T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ViewOverView_Depth1_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.v1 AS SELECT Id FROM dbo.T;
            GO
            CREATE VIEW dbo.v2 AS SELECT Id FROM dbo.v1;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ViewOverViewOverView_Depth2_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.v1 AS SELECT Id FROM dbo.T;
            GO
            CREATE VIEW dbo.v2 AS SELECT Id FROM dbo.v1;
            GO
            CREATE VIEW dbo.v3 AS SELECT Id FROM dbo.v2;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.v3", finding.ViewQualifiedName);
        Assert.Equal(2, finding.Depth);
        Assert.Equal(["dbo.v3", "dbo.v2", "dbo.v1"], finding.Chain);
        Assert.Equal(["dbo.T"], finding.BaseTables);
    }

    [Fact]
    public void DeeperChain_AllQualifyingViewsFire()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.v1 AS SELECT Id FROM dbo.T;
            GO
            CREATE VIEW dbo.v2 AS SELECT Id FROM dbo.v1;
            GO
            CREATE VIEW dbo.v3 AS SELECT Id FROM dbo.v2;
            GO
            CREATE VIEW dbo.v4 AS SELECT Id FROM dbo.v3;
            """);

        // v3 (depth 2) and v4 (depth 3) both qualify; v1/v2 do not.
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.ViewQualifiedName == "dbo.v3" && f.Depth == 2);
        Assert.Contains(findings, f => f.ViewQualifiedName == "dbo.v4" && f.Depth == 3);
    }

    [Fact]
    public void FanOutToMultipleBaseTables_AllListed()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.A (Id INT NOT NULL);
            GO
            CREATE TABLE dbo.B (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.v1 AS SELECT A.Id FROM dbo.A JOIN dbo.B ON A.Id = B.Id;
            GO
            CREATE VIEW dbo.v2 AS SELECT Id FROM dbo.v1;
            GO
            CREATE VIEW dbo.v3 AS SELECT Id FROM dbo.v2;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(["dbo.A", "dbo.B"], finding.BaseTables);
    }
}
