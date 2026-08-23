using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Verify.Catalog;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CatalogLifecycleOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CatalogLifecycleOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) NOT NULL);
        GO
        DROP TABLE dbo.Orders;
        GO
        CREATE TABLE dbo.Orders (OrderCode NVARCHAR(20) NOT NULL);
        GO
        CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
        GO
        DROP INDEX IX_Orders_OrderCode ON dbo.Orders;
        GO
        CREATE VIEW dbo.vw_Orders AS SELECT OrderCode FROM dbo.Orders;
        GO
        DROP VIEW dbo.vw_Orders;
        GO
        CREATE VIEW dbo.vw_Orders AS SELECT OrderCode FROM dbo.Orders;
        """;

    private static (DatabaseCatalog Catalog, LineageCatalog Lineage) BuildStaticCatalog(string sql)
    {
        var result = SqlScriptParser.ParseText("migration.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return (catalog, lineage);
    }

    [Fact]
    public async Task RealServer_DropTableThenRecreateWithDifferentType_HasTheRecreatedType()
    {

        var real = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = Assert.Single(real.Tables, t => t.Name == "Orders");
        Assert.Equal(SqlTypeCategory.NVarChar, table.FindColumn("OrderCode")!.Type!.Category);
    }

    [Fact]
    public async Task RealServer_DroppedIndex_ColumnIsNotIndexed()
    {
        var real = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var table = Assert.Single(real.Tables, t => t.Name == "Orders");
        Assert.False(table.IsIndexedColumn("OrderCode"));
    }

    [Fact]
    public async Task StaticCatalog_AgreesWithRealServer_AfterDropTableRecreateCycle()
    {
        var real = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var (staticCatalog, _) = BuildStaticCatalog(Ddl);

        var realTable = Assert.Single(real.Tables, t => t.Name == "Orders");
        var staticTable = staticCatalog.Find("dbo.Orders");

        Assert.NotNull(staticTable);
        Assert.Equal(realTable.FindColumn("OrderCode")!.Type!.Category, staticTable!.FindColumn("OrderCode")!.Type!.Category);
    }

    [Fact]
    public async Task StaticCatalog_AgreesWithRealServer_IndexDroppedOnBothSides()
    {
        var real = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var (staticCatalog, _) = BuildStaticCatalog(Ddl);

        var realTable = Assert.Single(real.Tables, t => t.Name == "Orders");
        var staticTable = staticCatalog.Find("dbo.Orders")!;

        Assert.False(realTable.IsIndexedColumn("OrderCode"));
        Assert.False(staticTable.IsIndexedColumn("OrderCode"));
    }

    [Fact]
    public void StaticLineage_ViewDroppedThenRecreated_ResolvesToTheRecreatedDefinition()
    {
        var (_, lineage) = BuildStaticCatalog(Ddl);

        var view = lineage.Find("dbo.vw_Orders");
        Assert.NotNull(view);
        Assert.NotNull(view!.FindColumn("OrderCode"));
    }
}
