using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LineageParityCheckerTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanLineageParityCheckerTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly LineageParityChecker _checker;

    public LineageParityCheckerTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _checker = new LineageParityChecker(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, Attribute SQL_VARIANT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderCode FROM dbo.Orders;
            GO
            CREATE VIEW dbo.vw_CastOrders AS SELECT CAST(OrderCode AS NVARCHAR(50)) AS OrderCode FROM dbo.Orders;
            GO
            CREATE VIEW dbo.vw_ExprOrders AS SELECT UPPER(OrderCode) AS OrderCode FROM dbo.Orders;
            GO
            CREATE VIEW dbo.vw_VariantOrders AS SELECT Attribute FROM dbo.Orders;
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private static LineageCatalog Catalog(string qualifiedName, string columnName, ColumnProvenance provenance) =>
        new(
            new Dictionary<string, ResolvedRelation>
            {
                [qualifiedName] = new ResolvedRelation(qualifiedName, [new ResolvedColumn(columnName, provenance)]),
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new SkipLedger());

    [Fact]
    public async Task CheckAsync_BaseColumnMatchesRealCatalog_NoMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_BaseColumnWrongLength_ReportsLengthMismatch()
    {

        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("length", mismatch.Facet);
        Assert.Equal("50", mismatch.InferredValue);
        Assert.Equal("20", mismatch.ActualValue);
    }

    [Fact]
    public async Task CheckAsync_CastProvenanceMatchesRealView_NoMismatch()
    {

        var lineage = Catalog(
            "dbo.vw_CastOrders", "OrderCode",
            new ColumnProvenance.Cast(
                new SqlType(SqlTypeCategory.NVarChar, Length: 50, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_CastProvenanceWrongTargetType_ReportsCategoryMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_CastOrders", "OrderCode",
            new ColumnProvenance.Cast(
                new SqlType(SqlTypeCategory.Int),
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("category", mismatch.Facet);
        Assert.Equal("Int", mismatch.InferredValue);
        Assert.Equal("nvarchar", mismatch.ActualValue);
    }

    [Fact]
    public async Task CheckAsync_SqlVariantBaseColumnMatchesRealCatalog_NoMismatch()
    {

        var lineage = Catalog(
            "dbo.vw_VariantOrders", "Attribute",
            new ColumnProvenance.BaseColumn("dbo.Orders", "Attribute", new SqlType(SqlTypeCategory.SqlVariant)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_ExpressionProvenanceWithNoInferredType_SkipsRatherThanGuessing()
    {

        var lineage = Catalog(
            "dbo.vw_ExprOrders", "OrderCode",
            new ColumnProvenance.Expression(
                InferredType: null,
                Inputs: [new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))]));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_UnionAllBranchesAgree_ChecksTheAgreedType()
    {
        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.Union(
            [
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 999, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))),
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 999, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))),
            ]));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("length", mismatch.Facet);
        Assert.Equal("999", mismatch.InferredValue);
    }

    [Fact]
    public async Task CheckAsync_NullInferredCollation_SkipsFacetRatherThanReportingMismatch()
    {

        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: null)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_UnicodeColumnLength_AccountsForByteDoublingInSysColumns()
    {
        await new ScriptDeployer(_options).DeployAsync(
            "CREATE VIEW dbo.vw_UnicodeCheck AS SELECT CAST(OrderCode AS NVARCHAR(30)) AS OrderCode FROM dbo.Orders;",
            DatabaseName);

        var lineage = Catalog(
            "dbo.vw_UnicodeCheck", "OrderCode",
            new ColumnProvenance.Cast(
                new SqlType(SqlTypeCategory.NVarChar, Length: 30, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
                new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }
}
