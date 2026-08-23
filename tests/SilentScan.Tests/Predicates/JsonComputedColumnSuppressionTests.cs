using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class JsonComputedColumnSuppressionTests : OracleTestFixture
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tier1");

    private static string ReadFixture(string fileName) => File.ReadAllText(Path.Combine(FixturesDir, fileName));

    protected override string DatabaseNameSeed => nameof(JsonComputedColumnSuppressionTests);

    protected override string Ddl => ReadFixture("FUNCTION_WRAPPED_COLUMN_json_value_clean.sql");

    [Fact]
    public void MatchingIndexedComputedColumn_SuppressesTheFinding_FileModeCatalog()
    {
        var sql = ReadFixture("FUNCTION_WRAPPED_COLUMN_json_value_clean.sql");
        var parseResult = SqlScriptParser.ParseText("json_value_clean.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public void DifferentPathIndexedComputedColumn_DoesNotSuppress_FileModeCatalog()
    {
        var sql = ReadFixture("FUNCTION_WRAPPED_COLUMN_json_value_different_path_fires.sql");
        var parseResult = SqlScriptParser.ParseText("json_value_different_path_fires.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("Payload", finding.ColumnName);
        Assert.Equal("JSON_VALUE", finding.Detail);
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_SuppressesThroughLiveEngineAuthoritativePipeline()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Ddl);

        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_OracleConfirmsIndexSeek()
    {
        const string probe = "SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status') = 'ACTIVE';";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_MaxTypedComparisonValue_StillSeeksButThroughGetRangeWithMismatchedTypes()
    {
        const string probe = "DECLARE @p NVARCHAR(MAX) = N'ACTIVE'; SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status') = @p;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
        Assert.Contains("GetRangeWithMismatchedTypes", planXml);
    }
}
