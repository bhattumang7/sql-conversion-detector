using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class CompositeIndexLeadingColumnScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns, IReadOnlyList<CatalogIndex> indexes) =>
        new(schema, name, CatalogTableKind.Table, columns, indexes, SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn Col(string name) => new(name, new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false);

    private static IReadOnlyList<CompositeIndexLeadingColumnFinding> Scan(string sql, DatabaseCatalog catalog)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CompositeIndexLeadingColumnScanner.Scan(result, catalog);
    }

    private static DatabaseCatalog CatalogWithComposite(params CatalogIndex[] extraIndexes)
    {
        var indexes = new List<CatalogIndex>
        {
            new("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, KeyColumns: ["OrderId"], IncludedColumns: []),
            new("IX_Orders_Region_Status", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Region", "Status"], IncludedColumns: []),
        };
        indexes.AddRange(extraIndexes);

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("Region"), Col("Status")], indexes));
        return catalog;
    }

    [Fact]
    public void PredicateOnNonLeadingColumnOnly_LeadingUnconstrainedAnywhere_Fires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", CatalogWithComposite());

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal("IX_Orders_Region_Status", finding.IndexName);
        Assert.Equal("Status", finding.ViolatingColumnName);
        Assert.Equal(1, finding.ViolatingColumnPosition);
    }

    [Fact]
    public void PredicateOnLeadingColumnToo_NeverFires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Region = 1 AND Status = 5;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void PredicateOnLeadingColumnWithDifferentCasingThanDdl_StillSuppresses()
    {

        var findings = Scan("SELECT 1 FROM DBO.ORDERS WHERE Region = 1 AND Status = 5;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void CteSharesNameWithIndexedBaseTable_NeverBindsToTheBaseTable()
    {

        var findings = Scan(
            "WITH Orders AS (SELECT 1 AS Region, 2 AS Status) SELECT 1 FROM Orders WHERE Status = 5;",
            CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void LeadingColumnReferencedOnlyInsideOrBranch_StillSuppresses()
    {

        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5 AND (Region = 1 OR Region = 2);", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void PredicateOnNonLeadingColumnOnlyReachableThroughOr_NeverFires()
    {

        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5 OR OrderId = 1;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void AlternativeIndexLeadsWithTheSameViolatingColumn_Suppresses()
    {
        var extra = new CatalogIndex("IX_Orders_Status", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Status"], IncludedColumns: []);
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", CatalogWithComposite(extra));

        Assert.Empty(findings);
    }

    [Fact]
    public void SingleColumnIndex_NeverConsidered()
    {

        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE OrderId = 5;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void FilteredIndex_NeverConsidered()
    {
        var filtered = new CatalogIndex("IX_Orders_Region_Status_Filtered", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Region", "Status"], IncludedColumns: [], IsFiltered: true);
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("Region"), Col("Status")], [filtered]));

        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void JoinOnClauseSuppliesTheLeadingColumn_Suppresses()
    {
        var catalog = CatalogWithComposite();
        catalog.AddOrReplace(Table("dbo", "Regions", [Col("RegionId")], []));

        var findings = Scan(
            "SELECT 1 FROM dbo.Orders o JOIN dbo.Regions r ON o.Region = r.RegionId WHERE o.Status = 5;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateStatement_Fires()
    {
        var findings = Scan("UPDATE dbo.Orders SET OrderId = OrderId WHERE Status = 5;", CatalogWithComposite());

        Assert.Single(findings);
    }

    [Fact]
    public void DeleteStatement_Fires()
    {
        var findings = Scan("DELETE FROM dbo.Orders WHERE Status = 5;", CatalogWithComposite());

        Assert.Single(findings);
    }

    [Fact]
    public void WildcardColumnReferenceInsideNestedSubquery_NeverCrashes()
    {

        var findings = Scan(
            """
            SELECT 1 FROM dbo.Orders
            WHERE Region = 1 AND Status = 5
              AND OrderId = (SELECT TOP 1 OrderId FROM dbo.Orders WHERE (SELECT COUNT(*) FROM dbo.Orders) > 0);
            """,
            CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void LeadingColumnReferencedOnlyFromNestedExistsAgainstOuterAlias_CountsAsReferencedAnywhere()
    {
        var catalog = CatalogWithComposite();
        catalog.AddOrReplace(Table("dbo", "OrderLines", [Col("OrderId")], []));

        var findings = Scan(
            "SELECT 1 FROM dbo.Orders o WHERE Status = 5 "
            + "AND EXISTS (SELECT 1 FROM dbo.OrderLines ol WHERE ol.OrderId = o.Region);",
            catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void ContradictionOnUnrelatedColumn_MakesWhereClauseUnsatisfiable_Suppresses()
    {
        var findings = Scan(
            "SELECT 1 FROM dbo.Orders WHERE Status = 5 AND OrderId = 1 AND OrderId = 2;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void ThreeColumnIndex_ViolatingColumnAtLaterPosition_ReportsCorrectPosition()
    {
        var threeCol = new CatalogIndex("IX_Orders_A_B_C", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Region", "Status", "OrderId"], IncludedColumns: []);
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("Region"), Col("Status")], [threeCol]));

        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE OrderId = 5;", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("OrderId", finding.ViolatingColumnName);
        Assert.Equal(2, finding.ViolatingColumnPosition);
    }
}
