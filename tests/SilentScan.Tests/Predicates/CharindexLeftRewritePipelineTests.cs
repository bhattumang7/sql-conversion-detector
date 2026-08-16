using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream" #3:
/// CHARINDEX(x, col) = 1 / LEFT(col, n) = 'x' are both exactly equivalent to col LIKE 'x%' - this
/// proves the rewrite itself is real (the rewritten form actually seeks), not just that the
/// original form scans, plus the same computed-column precision guard every other rule in this
/// stream shares.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class CharindexLeftRewritePipelineTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CharindexLeftRewritePipelineTests);

    private const string GuardSql = """
        CREATE TABLE dbo.GuardCustomers (
            Code VARCHAR(50) NOT NULL,
            CodePrefixPosition AS CHARINDEX('AB', Code));
        """;

    protected override string Ddl => """
        CREATE TABLE dbo.Customers (CustomerId INT NOT NULL PRIMARY KEY, Code VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Customers_Code (Code));
        GO
        CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, Sku VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Products_Sku (Sku));
        """;

    [Fact]
    public async Task CharindexOriginalForm_OracleConfirmsIndexScan()
    {
        const string probe = "SELECT CustomerId FROM dbo.Customers WHERE CHARINDEX('AB', Code) = 1;";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task CharindexRewrittenForm_OracleConfirmsIndexSeek()
    {
        const string probe = "SELECT CustomerId FROM dbo.Customers WHERE Code LIKE 'AB%';";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task LeftOriginalForm_OracleConfirmsIndexScan()
    {
        const string probe = "SELECT ProductId FROM dbo.Products WHERE LEFT(Sku, 3) = 'ABC';";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task LeftRewrittenForm_OracleConfirmsIndexSeek()
    {
        const string probe = "SELECT ProductId FROM dbo.Products WHERE Sku LIKE 'ABC%';";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public void MatchingIndexedComputedColumn_SuppressesTheFinding_FileModeCatalog()
    {
        const string sql = GuardSql + "\nGO\nCREATE INDEX IX_GuardCustomers_Check ON dbo.GuardCustomers(CodePrefixPosition);\nGO\nSELECT 1 FROM dbo.GuardCustomers WHERE CHARINDEX('AB', Code) = 1;";
        var parseResult = SqlScriptParser.ParseText("guard.sql", sql);
        Assert.False(parseResult.HasErrors);

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }
}
