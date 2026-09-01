using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DateFunctionSynonymColumnPipelineTests : OracleTestFixture
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tier1");

    private static string ReadFixture(string fileName) => File.ReadAllText(Path.Combine(FixturesDir, fileName));

    protected override string DatabaseNameSeed => nameof(DateFunctionSynonymColumnPipelineTests);

    protected override string Ddl => ReadFixture("DATE_DATEPART_SYNONYM_ON_COLUMN_clean.sql");

    [Fact]
    public void MatchingIndexedComputedColumn_SynonymUnitSpelling_SuppressesTheFinding_FileModeCatalog()
    {
        var sql = ReadFixture("DATE_DATEPART_SYNONYM_ON_COLUMN_clean.sql");
        var parseResult = SqlScriptParser.ParseText("date_datepart_synonym_clean.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_SynonymUnitSpelling_SuppressesThroughLiveEngineAuthoritativePipeline()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Ddl);

        Assert.DoesNotContain(report.Find<SargabilityFinding>("NonSargablePredicateScanner"), f => f.ColumnName == "OrderDate");
    }

    [Fact]
    public async Task ShorthandYearPredicate_OracleConfirmsIndexSeekAgainstSynonymDefinedComputedColumn()
    {
        const string probe = "SELECT OrderId FROM dbo.Orders WHERE YEAR(OrderDate) = 2024;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task DirectDatepartSynonymPredicate_OracleConfirmsIndexSeekAgainstSynonymDefinedComputedColumn()
    {
        const string probe = "SELECT OrderId FROM dbo.Orders WHERE DATEPART(year, OrderDate) = 2024;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public void DirectDatepartSynonymPredicate_SuppressesTheFinding_FileModeCatalog()
    {
        const string sql = """
            CREATE TABLE dbo.Orders
            (
                OrderId    INT NOT NULL PRIMARY KEY,
                OrderDate  DATETIME NOT NULL,
                OrderYear  AS DATEPART(yy, OrderDate)
            );
            GO
            CREATE INDEX IX_Orders_OrderYear ON dbo.Orders(OrderYear);
            GO

            SELECT OrderId
            FROM dbo.Orders
            WHERE DATEPART(year, OrderDate) = 2024;
            """;
        var parseResult = SqlScriptParser.ParseText("date_datepart_direct_synonym_clean.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public void DirectDatepartMismatchedUnit_StillFires_FileModeCatalog()
    {
        const string sql = """
            CREATE TABLE dbo.Orders
            (
                OrderId    INT NOT NULL PRIMARY KEY,
                OrderDate  DATETIME NOT NULL,
                OrderMonth AS DATEPART(mm, OrderDate)
            );
            GO
            CREATE INDEX IX_Orders_OrderMonth ON dbo.Orders(OrderMonth);
            GO

            SELECT OrderId
            FROM dbo.Orders
            WHERE DATEPART(year, OrderDate) = 2024;
            """;
        var parseResult = SqlScriptParser.ParseText("date_datepart_direct_mismatch.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("OrderDate", finding.ColumnName);
    }

    [Fact]
    public void MismatchedComputedColumnUnit_StillFiresAndOracleConfirmsIndexScan_FileModeCatalog()
    {
        var sql = ReadFixture("DATE_DATEPART_SYNONYM_MISMATCH_fires.sql");
        var parseResult = SqlScriptParser.ParseText("date_datepart_synonym_mismatch.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("OrderDate", finding.ColumnName);
    }

    [Fact]
    public void MonthSynonymComputedColumn_SuppressesTheFinding_FileModeCatalog()
    {
        var sql = ReadFixture("DATE_DATEPART_SYNONYM_MONTH_ON_COLUMN_clean.sql");
        var parseResult = SqlScriptParser.ParseText("date_datepart_synonym_month_clean.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task MonthSynonymComputedColumn_OracleConfirmsIndexSeek()
    {
        var monthSql = ReadFixture("DATE_DATEPART_SYNONYM_MONTH_ON_COLUMN_clean.sql");
        var databaseName = DatabaseName + "Month";
        await new SilentScan.Verify.Deployment.DatabaseProvisioner(Options).CreateFreshAsync(databaseName);
        try
        {
            await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployAsync(monthSql, databaseName);
            const string probe = "SELECT OrderId FROM dbo.Orders WHERE MONTH(OrderDate) = 6;";

            var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(databaseName, probe);

            Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
        }
        finally
        {
            await new SilentScan.Verify.Deployment.DatabaseProvisioner(Options).DropIfExistsAsync(databaseName);
        }
    }

    [Fact]
    public void DaySynonymComputedColumn_SuppressesTheFinding_FileModeCatalog()
    {
        var sql = ReadFixture("DATE_DATEPART_SYNONYM_DAY_ON_COLUMN_clean.sql");
        var parseResult = SqlScriptParser.ParseText("date_datepart_synonym_day_clean.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DaySynonymComputedColumn_OracleConfirmsIndexSeek()
    {
        var daySql = ReadFixture("DATE_DATEPART_SYNONYM_DAY_ON_COLUMN_clean.sql");
        var databaseName = DatabaseName + "Day";
        await new SilentScan.Verify.Deployment.DatabaseProvisioner(Options).CreateFreshAsync(databaseName);
        try
        {
            await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployAsync(daySql, databaseName);
            const string probe = "SELECT OrderId FROM dbo.Orders WHERE DAY(OrderDate) = 15;";

            var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(databaseName, probe);

            Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
        }
        finally
        {
            await new SilentScan.Verify.Deployment.DatabaseProvisioner(Options).DropIfExistsAsync(databaseName);
        }
    }

    [Fact]
    public async Task MismatchedComputedColumnUnit_OracleConfirmsIndexScanNotSeek()
    {
        var mismatchSql = ReadFixture("DATE_DATEPART_SYNONYM_MISMATCH_fires.sql");
        var databaseName = DatabaseName + "Mismatch";
        await new SilentScan.Verify.Deployment.DatabaseProvisioner(Options).CreateFreshAsync(databaseName);
        try
        {
            await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployAsync(mismatchSql, databaseName);
            const string probe = "SELECT OrderId FROM dbo.Orders WHERE YEAR(OrderDate) = 2024;";

            var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(databaseName, probe);

            Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
        }
        finally
        {
            await new SilentScan.Verify.Deployment.DatabaseProvisioner(Options).DropIfExistsAsync(databaseName);
        }
    }
}
