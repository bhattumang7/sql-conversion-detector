using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Roadmap Phase A1 (catalog lifecycle): DROP TABLE/DROP INDEX/DROP VIEW previously left a
/// stale definition in the catalog forever, a real false-positive risk for any repo whose
/// scanned file set includes a migration history (drop-and-recreate-with-a-different-type is
/// exactly the pattern this tool exists to catch, so getting it backwards would be its own
/// bug). This doesn't just assert the static catalog "looks right" in isolation - it deploys
/// the identical script to the real disposable oracle and cross-checks
/// <see cref="CatalogBuilder"/>'s output against <see cref="LiveCatalogReader"/> reading the
/// real <c>sys.columns</c>/<c>sys.indexes</c> after the same DROP/CREATE sequence actually ran.
/// </summary>
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
        // Sanity check of the Ddl script itself against the real engine, not of this tool's
        // code - confirms the fixture actually exercises what it claims to.
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
