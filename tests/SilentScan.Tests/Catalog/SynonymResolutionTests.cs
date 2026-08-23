using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.Common;

namespace SilentScan.Tests.Catalog;

[Trait("Category", "Oracle")]
public sealed class SynonymResolutionTests
{
    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task SynonymForTable_ResolvesToTheRealBaseTable()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL, INDEX IX_Sku (Sku));
            GO
            CREATE SYNONYM dbo.Stock FOR dbo.Inventory;
            GO
            SELECT 1 FROM dbo.Stock WHERE Sku = N'S1';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Sku");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Inventory", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task SynonymForView_Resolves_EvenThoughViewsAreNeverInDatabaseCatalog()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL, INDEX IX_Sku (Sku));
            GO
            CREATE VIEW dbo.vInventory AS SELECT Sku FROM dbo.Inventory;
            GO
            CREATE SYNONYM dbo.StockView FOR dbo.vInventory;
            GO
            SELECT 1 FROM dbo.StockView WHERE Sku = N'S1';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Sku");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Inventory", finding.Column.TableQualifiedName);
        Assert.Equal(1, finding.Column.Depth);
    }

    [Fact]
    public async Task ViewDefinedOverSynonymForAnotherView_GetsACorrectDependencyEdge()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL, INDEX IX_Sku (Sku));
            GO
            CREATE VIEW dbo.vInner AS SELECT Sku FROM dbo.Inventory;
            GO
            CREATE SYNONYM dbo.SynInner FOR dbo.vInner;
            GO
            CREATE VIEW dbo.vOuter AS SELECT Sku FROM dbo.SynInner;
            GO
            SELECT 1 FROM dbo.vOuter WHERE Sku = N'S1';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Sku");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Inventory", finding.Column.TableQualifiedName);
    }

    [Fact]
    public async Task DropSynonym_MakesTheNameUnresolvedAgain()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL);
            GO
            CREATE SYNONYM dbo.Stock FOR dbo.Inventory;
            GO
            DROP SYNONYM dbo.Stock;
            GO
            SELECT 1 FROM dbo.Stock WHERE Sku = N'S1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Contains(report.SkippedConstructs, s => s.Reason.Contains("dbo.Stock", StringComparison.Ordinal) && s.Reason.Contains("has no known DDL", StringComparison.Ordinal));
    }

private static ScanReport ScanParsedOnly(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("synonym.sql", sql);
        Assert.Empty(parseResult.Errors);
        var catalog = CatalogBuilder.Build([parseResult]);
        return ScanReportBuilder.BuildFromParseResults([parseResult], catalog);
    }

    [Fact]
    public void SynonymCycle_FallsBackToTheOriginalNameRatherThanLooping()
    {
        var report = ScanParsedOnly("""
            CREATE SYNONYM dbo.A FOR dbo.B;
            GO
            CREATE SYNONYM dbo.B FOR dbo.A;
            GO
            SELECT 1 FROM dbo.A WHERE Sku = N'S1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Contains(report.SkippedConstructs, s => s.Reason.Contains("dbo.A", StringComparison.Ordinal));
    }

    [Fact]
    public void FourPartLinkedServerSynonym_IsLedgeredNotMisregistered()
    {
        var report = ScanParsedOnly("""
            CREATE SYNONYM dbo.RemoteStock FOR linkedserver.otherdb.dbo.RemoteInventory;
            GO
            SELECT 1 FROM dbo.RemoteStock WHERE Sku = N'S1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Contains(report.SkippedConstructs, s => s.ConstructKind == "CREATE SYNONYM" && s.Reason.Contains("linked server", StringComparison.Ordinal));
    }
}
