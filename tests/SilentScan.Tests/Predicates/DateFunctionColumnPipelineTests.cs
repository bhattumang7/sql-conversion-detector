using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DateFunctionColumnPipelineTests : OracleTestFixture
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tier1");

    private static string ReadFixture(string fileName) => File.ReadAllText(Path.Combine(FixturesDir, fileName));

    protected override string DatabaseNameSeed => nameof(DateFunctionColumnPipelineTests);

    protected override string Ddl => ReadFixture("DATE_YEAR_ON_COLUMN_clean.sql");

    [Fact]
    public void MatchingIndexedComputedColumn_SuppressesTheFinding_FileModeCatalog()
    {
        var sql = ReadFixture("DATE_YEAR_ON_COLUMN_clean.sql");
        var parseResult = SqlScriptParser.ParseText("date_year_clean.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_SuppressesThroughLiveEngineAuthoritativePipeline()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Ddl);

        Assert.DoesNotContain(report.Find<SargabilityFinding>("NonSargablePredicateScanner"), f => f.ColumnName == "OrderDate");
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_OracleConfirmsIndexSeek()
    {
        const string probe = "SELECT OrderId FROM dbo.Orders WHERE YEAR(OrderDate) = 2024;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task NoComputedColumn_OracleConfirmsIndexScan()
    {
        var noGuardSql = ReadFixture("DATE_YEAR_ON_COLUMN_fires.sql");
        var databaseName = DatabaseName + "NoGuard";
        await new SilentScan.Verify.Deployment.DatabaseProvisioner(Options).CreateFreshAsync(databaseName);
        try
        {
            await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployAsync(noGuardSql, databaseName);
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
