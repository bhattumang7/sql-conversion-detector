using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class JsonIndexRewriteTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tier1");

    private static IReadOnlyList<JsonIndexRewriteFinding> Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("json_index_rewrite.sql", sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        return NonSargablePredicateScanner.ScanFull(parseResult, catalog, lineage).JsonIndexRewriteFindings;
    }

    [Fact]
    public void JsonValueEqualityOnJsonIndexedColumn_Fires()
    {
        var sql = File.ReadAllText(Path.Combine(FixturesDir, "JSON_INDEX_REWRITE_ELIGIBLE_fires.sql"));

        var finding = Assert.Single(Scan(sql));
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal("Payload", finding.ColumnName);
        Assert.Equal("$.status", finding.JsonPath);
    }

    [Fact]
    public void LiteralOnLeftHandSide_StillFires()
    {
        const string sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload JSON NOT NULL);
            CREATE JSON INDEX IX_Orders_Payload ON dbo.Orders(Payload);
            SELECT OrderId FROM dbo.Orders WHERE 'shipped' = JSON_VALUE(Payload, '$.status');
            """;

        Assert.Single(Scan(sql));
    }

    [Fact]
    public void ReturningClauseVariant_StillFires()
    {
        const string sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload JSON NOT NULL);
            CREATE JSON INDEX IX_Orders_Payload ON dbo.Orders(Payload);
            SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status' RETURNING VARCHAR(50)) = 'shipped';
            """;

        Assert.Single(Scan(sql));
    }

    [Fact]
    public void NoJsonIndexOnColumn_NeverFires()
    {
        const string sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload JSON NOT NULL);
            SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status') = 'shipped';
            """;

        Assert.Empty(Scan(sql));
    }

    [Fact]
    public void JsonIndexOnADifferentColumn_NeverFires()
    {
        const string sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload JSON NOT NULL, Extra JSON NOT NULL);
            CREATE JSON INDEX IX_Orders_Extra ON dbo.Orders(Extra);
            SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status') = 'shipped';
            """;

        Assert.Empty(Scan(sql));
    }

    [Fact]
    public void JsonQueryInsteadOfJsonValue_NeverFires()
    {
        const string sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload JSON NOT NULL);
            CREATE JSON INDEX IX_Orders_Payload ON dbo.Orders(Payload);
            SELECT OrderId FROM dbo.Orders WHERE JSON_QUERY(Payload, '$.status') = '"shipped"';
            """;

        Assert.Empty(Scan(sql));
    }

    [Fact]
    public void NonEqualityComparison_NeverFires()
    {
        const string sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload JSON NOT NULL);
            CREATE JSON INDEX IX_Orders_Payload ON dbo.Orders(Payload);
            SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status') > 'shipped';
            """;

        Assert.Empty(Scan(sql));
    }

    [Fact]
    public void DisabledJsonIndex_NeverFires()
    {
        const string sql = """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload JSON NOT NULL);
            CREATE JSON INDEX IX_Orders_Payload ON dbo.Orders(Payload);
            ALTER INDEX IX_Orders_Payload ON dbo.Orders DISABLE;
            SELECT OrderId FROM dbo.Orders WHERE JSON_VALUE(Payload, '$.status') = 'shipped';
            """;

        Assert.Empty(Scan(sql));
    }
}
