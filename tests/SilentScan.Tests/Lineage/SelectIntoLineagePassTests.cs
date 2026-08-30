using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Lineage;

[Trait("Category", "Oracle")]
public sealed class SelectIntoLineagePassTests
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
    public async Task SelectIntoFromView_TargetColumnTypesFromTheViewsRealColumn()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Employees (Badge varchar(20) NOT NULL, INDEX IX_Badge (Badge));
            GO
            CREATE VIEW dbo.vEmployees AS SELECT Badge FROM dbo.Employees;
            GO
            CREATE PROCEDURE dbo.usp_Snapshot AS
            BEGIN
                SELECT Badge INTO #snap FROM dbo.vEmployees;
                SELECT 1 FROM #snap WHERE Badge = N'B1';
            END;
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Badge");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("#snap", finding.Column.TableQualifiedName);

        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public async Task SelectIntoFromUnionSource_ResolvesInstead_OfGivingUp()
    {

        var report = await Scan("""
            CREATE TABLE dbo.CurrentOrders (OrderCode varchar(20) NOT NULL);
            GO
            CREATE TABLE dbo.ArchivedOrders (OrderCode varchar(20) NOT NULL);
            GO
            SELECT OrderCode INTO #allOrders
            FROM dbo.CurrentOrders
            UNION ALL
            SELECT OrderCode FROM dbo.ArchivedOrders;
            GO
            SELECT 1 FROM #allOrders WHERE OrderCode = N'X1';
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "OrderCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public async Task AlterTableAddAfterSelectInto_SurvivesTheMerge()
    {

        var report = await Scan("""
            CREATE TABLE dbo.Employees (Badge varchar(20) NOT NULL);
            GO
            CREATE VIEW dbo.vEmployees AS SELECT Badge FROM dbo.Employees;
            GO
            CREATE PROCEDURE dbo.usp_Snapshot AS
            BEGIN
                SELECT Badge INTO #snap FROM dbo.vEmployees;
                ALTER TABLE #snap ADD SnapshotId INT NOT NULL;
                SELECT 1 FROM #snap WHERE Badge = N'B1';
            END;
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Badge");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public async Task CreateIndexAfterSelectInto_SurvivesTheMerge_ReportsIndexed()
    {

        var report = await Scan("""
            CREATE TABLE dbo.Employees (Badge varchar(20) NOT NULL);
            GO
            CREATE VIEW dbo.vEmployees AS SELECT Badge FROM dbo.Employees;
            GO
            CREATE PROCEDURE dbo.usp_Snapshot AS
            BEGIN
                SELECT Badge INTO #snap FROM dbo.vEmployees;
                CREATE INDEX IX_Snap_Badge ON #snap (Badge);
                SELECT 1 FROM #snap WHERE Badge = N'B1';
            END;
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Badge");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task SelectIntoFromBaseTable_UnaffectedByThePass()
    {

        var report = await Scan("""
            CREATE TABLE dbo.Employees (Badge varchar(20) NOT NULL, INDEX IX_Badge (Badge));
            GO
            SELECT Badge INTO #snap FROM dbo.Employees;
            GO
            SELECT 1 FROM #snap WHERE Badge = N'B1';
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Badge");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }
}
