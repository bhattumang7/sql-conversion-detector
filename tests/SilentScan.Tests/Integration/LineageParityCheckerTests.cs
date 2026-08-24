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
            CREATE TABLE dbo.Ledger (Amount DECIMAL(9, 2) NOT NULL, Content VARBINARY(20) NOT NULL, Notes VARCHAR(MAX) NOT NULL);
            GO
            CREATE VIEW dbo.vw_Ledger AS SELECT Amount, Content, Notes FROM dbo.Ledger;
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

    private static LineageCatalog Catalog(
        IReadOnlyDictionary<string, ResolvedRelation> relations,
        IEnumerable<string>? cyclicViews = null) =>
        new(
            relations,
            new HashSet<string>(cyclicViews ?? [], StringComparer.OrdinalIgnoreCase),
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

    [Fact]
    public async Task CheckAsync_ViewMarkedCyclic_SkippedEvenThoughItsTypeIsWrong()
    {
        var relations = new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.vw_Orders"] = new ResolvedRelation(
                "dbo.vw_Orders",
                [new ResolvedColumn("OrderCode", new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.Int)))]),
            ["dbo.vw_CastOrders"] = new ResolvedRelation(
                "dbo.vw_CastOrders",
                [new ResolvedColumn("OrderCode", new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.Int), new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))))]),
        };
        var lineage = Catalog(relations, cyclicViews: ["dbo.vw_Orders"]);

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("dbo.vw_CastOrders", mismatch.QualifiedViewName);
        Assert.Equal("category", mismatch.Facet);
    }

    [Fact]
    public async Task CheckAsync_ColumnAbsentFromOracleCatalog_SkipsThatColumnButStillReportsSiblingMismatch()
    {
        var relation = new ResolvedRelation(
            "dbo.vw_Orders",
            [
                new ResolvedColumn("NoSuchColumn", new ColumnProvenance.BaseColumn("dbo.Orders", "NoSuchColumn", new SqlType(SqlTypeCategory.Int))),
                new ResolvedColumn("OrderCode", new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.Int))),
            ]);
        var lineage = Catalog(new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase) { ["dbo.vw_Orders"] = relation });

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("OrderCode", mismatch.ColumnName);
        Assert.Equal("category", mismatch.Facet);
    }

    [Fact]
    public async Task CheckAsync_ObjectDoesNotExistInOracleCatalog_SkipsRelationEntirely()
    {
        var relations = new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.vw_DoesNotExist"] = new ResolvedRelation(
                "dbo.vw_DoesNotExist",
                [new ResolvedColumn("OrderCode", new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.Int)))]),
            ["dbo.vw_Orders"] = new ResolvedRelation(
                "dbo.vw_Orders",
                [new ResolvedColumn("OrderCode", new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.Int)))]),
        };
        var lineage = Catalog(relations);

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("dbo.vw_Orders", mismatch.QualifiedViewName);
    }

    [Fact]
    public async Task CheckAsync_DatabaseUnreachable_SwallowsSqlExceptionRatherThanThrowing()
    {
        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.Int)));

        var mismatches = await _checker.CheckAsync($"NoSuchDatabase_{Guid.NewGuid():N}", lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_DecimalWrongPrecision_ReportsPrecisionMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Ledger", "Amount",
            new ColumnProvenance.BaseColumn("dbo.Ledger", "Amount", new SqlType(SqlTypeCategory.Decimal, Precision: 12, Scale: 2)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("precision", mismatch.Facet);
        Assert.Equal("12", mismatch.InferredValue);
        Assert.Equal("9", mismatch.ActualValue);
    }

    [Fact]
    public async Task CheckAsync_DecimalWrongScale_ReportsScaleMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Ledger", "Amount",
            new ColumnProvenance.BaseColumn("dbo.Ledger", "Amount", new SqlType(SqlTypeCategory.Decimal, Precision: 9, Scale: 4)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("scale", mismatch.Facet);
        Assert.Equal("4", mismatch.InferredValue);
        Assert.Equal("2", mismatch.ActualValue);
    }

    [Fact]
    public async Task CheckAsync_DecimalWrongPrecisionAndScale_ReportsBothFacetsForSameColumn()
    {
        var lineage = Catalog(
            "dbo.vw_Ledger", "Amount",
            new ColumnProvenance.BaseColumn("dbo.Ledger", "Amount", new SqlType(SqlTypeCategory.Decimal, Precision: 18, Scale: 6)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Equal(2, mismatches.Count);
        Assert.Contains(mismatches, m => m.Facet == "precision");
        Assert.Contains(mismatches, m => m.Facet == "scale");
    }

    [Fact]
    public async Task CheckAsync_VarBinaryMatchingLength_NoMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Ledger", "Content",
            new ColumnProvenance.BaseColumn("dbo.Ledger", "Content", new SqlType(SqlTypeCategory.VarBinary, Length: 20)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_VarBinaryWrongLength_ReportsLengthMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Ledger", "Content",
            new ColumnProvenance.BaseColumn("dbo.Ledger", "Content", new SqlType(SqlTypeCategory.VarBinary, Length: 8)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("length", mismatch.Facet);
        Assert.Equal("8", mismatch.InferredValue);
        Assert.Equal("20", mismatch.ActualValue);
    }

    [Fact]
    public async Task CheckAsync_MaxLengthColumnMatchesRealMaxCatalogEntry_NoMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Ledger", "Notes",
            new ColumnProvenance.BaseColumn("dbo.Ledger", "Notes", new SqlType(SqlTypeCategory.VarChar, IsMax: true)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CheckAsync_InferredMaxButOracleColumnIsFixedLength_ReportsLengthMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Orders", "OrderCode",
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, IsMax: true)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("length", mismatch.Facet);
        Assert.Equal("MAX", mismatch.InferredValue);
        Assert.Equal("20", mismatch.ActualValue);
    }

    [Fact]
    public async Task CheckAsync_InferredFixedLengthButOracleColumnIsMax_ReportsLengthMismatch()
    {
        var lineage = Catalog(
            "dbo.vw_Ledger", "Notes",
            new ColumnProvenance.BaseColumn("dbo.Ledger", "Notes", new SqlType(SqlTypeCategory.VarChar, Length: 100)));

        var mismatches = await _checker.CheckAsync(DatabaseName, lineage);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("length", mismatch.Facet);
        Assert.Equal("100", mismatch.InferredValue);
        Assert.Equal("-1", mismatch.ActualValue);
    }
}
