using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Lineage;

/// <summary>
/// Regression coverage for the SELECT INTO-through-views gap (formerly pinned in
/// KnownGapCharacterizationTests.SelectIntoFromView_ColumnsStayUntyped_MismatchOnTempCopyIsSilentlyDropped):
/// Pass 1 (CatalogBuilder/SelectIntoColumnResolver) can only resolve a SELECT INTO target's
/// columns against tables already known to the catalog, since views are a Pass 2/Lineage
/// concept catalog-building can't depend on without inverting the pass order. SelectIntoLineagePass
/// closes that by re-resolving every target's columns once lineage exists. Runs through
/// <see cref="ScanReportBuilder"/>, the same entry point production uses.
/// </summary>
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

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Badge");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("#snap", finding.Column.TableQualifiedName);

        // A temp table copy never inherits the source's index - Indexed=false here is a real,
        // correct fact about #snap, not a resolution failure (dbo.Employees.Badge is indexed,
        // #snap.Badge is not).
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public async Task SelectIntoFromUnionSource_ResolvesInstead_OfGivingUp()
    {
        // The UNION give-up SelectIntoColumnResolver's own doc comment names is fixed for
        // free: QueryExpressionResolver already handles BinaryQueryExpression natively.
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

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "OrderCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public async Task AlterTableAddAfterSelectInto_SurvivesTheMerge()
    {
        // Pass 1's own ALTER TABLE #tmp ADD (a routine post-SELECT-INTO pattern) must not be
        // discarded by the merge - only column TYPES are filled in, never the column/index list.
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

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Badge");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public async Task CreateIndexAfterSelectInto_SurvivesTheMerge_ReportsIndexed()
    {
        // A CREATE INDEX issued after SELECT INTO (also routine) must survive the merge just
        // like ALTER TABLE ADD does - the fix only ever fills in null column types.
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

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Badge");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task SelectIntoFromBaseTable_UnaffectedByThePass()
    {
        // The common, already-working case (Pass 1 alone can resolve this) must keep working
        // identically - the new pass only fills in columns Pass 1 left null.
        var report = await Scan("""
            CREATE TABLE dbo.Employees (Badge varchar(20) NOT NULL, INDEX IX_Badge (Badge));
            GO
            SELECT Badge INTO #snap FROM dbo.Employees;
            GO
            SELECT 1 FROM #snap WHERE Badge = N'B1';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Badge");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }
}
