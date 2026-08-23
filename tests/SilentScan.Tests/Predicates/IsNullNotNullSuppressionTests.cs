using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class IsNullNotNullSuppressionTests : OracleTestFixture
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tier1");

    private static string ReadFixture(string fileName) => File.ReadAllText(Path.Combine(FixturesDir, fileName));

    protected override string DatabaseNameSeed => nameof(IsNullNotNullSuppressionTests);

    protected override string Ddl => ReadFixture("FUNCTION_WRAPPED_COLUMN_isnull_not_null_clean.sql");

    [Fact]
    public void NotNullColumn_SuppressesTheFinding_FileModeCatalog()
    {
        var sql = ReadFixture("FUNCTION_WRAPPED_COLUMN_isnull_not_null_clean.sql");
        var parseResult = SqlScriptParser.ParseText("isnull_not_null_clean.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task NotNullColumn_SuppressesThroughLiveEngineAuthoritativePipeline()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Ddl);

        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task NotNullColumn_OracleConfirmsIndexSeek()
    {
        const string probe = "SELECT OrderId FROM dbo.Orders WHERE ISNULL(Age, 0) = 0;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task NotNullColumn_WideningDefaultType_StillOracleConfirmsIndexSeek()
    {
        const string probe = "DECLARE @d BIGINT = 99999999999; SELECT OrderId FROM dbo.Orders WHERE ISNULL(Age, @d) = 0;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public void NullableColumn_DoesNotSuppress_FileModeCatalog()
    {
        const string sql = """
            CREATE TABLE dbo.NullableOrders (OrderId INT NOT NULL PRIMARY KEY, Age INT NULL);
            CREATE INDEX IX_NullableOrders_Age ON dbo.NullableOrders(Age);
            SELECT OrderId FROM dbo.NullableOrders WHERE ISNULL(Age, 0) = 0;
            """;
        var parseResult = SqlScriptParser.ParseText("nullable.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("Age", finding.ColumnName);
    }
}
