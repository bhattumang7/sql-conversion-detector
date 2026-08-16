using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream" #1:
/// ISNULL(col, x) on a NOT NULL column is a false positive the blanket function-wrap rule
/// doesn't catch - oracle-verified the optimizer proves ISNULL(NOT-NULL-col, x) = col and
/// simplifies the wrap away entirely, seeking directly on the column, regardless of the default
/// argument's own type (even a widening int-vs-bigint default still seeks). Needs a real catalog
/// (nullability is a DDL fact) - covered here rather than in the catalog-less
/// NonSargablePredicateScannerTests.
/// </summary>
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

        Assert.DoesNotContain(report.Tier1Findings, f => f.ColumnName == "Age");
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
        // The oracle claim is stronger than "the literal 0 happens to match the column's type" -
        // even a widening default (bigint default against an int column) still seeks, since the
        // simplification is a nullability fact, not a type one.
        const string probe = "DECLARE @d BIGINT = 99999999999; SELECT OrderId FROM dbo.Orders WHERE ISNULL(Age, @d) = 0;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public void NullableColumn_DoesNotSuppress_FileModeCatalog()
    {
        // Near-miss guard the other direction: a genuinely nullable column must still fire -
        // the suppression is nullability-gated, not a blanket ISNULL exemption.
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
