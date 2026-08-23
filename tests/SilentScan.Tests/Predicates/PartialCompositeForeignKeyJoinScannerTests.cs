using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class PartialCompositeForeignKeyJoinScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns, IReadOnlyList<CatalogIndex>? indexes = null) =>
        new(schema, name, CatalogTableKind.Table, columns, indexes ?? [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn Col(string name, SqlTypeCategory category = SqlTypeCategory.Int) =>
        new(name, new SqlType(category), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false);

private static DatabaseCatalog BuildCatalog(IReadOnlyList<CatalogIndex>? extraOrdersIndexes = null)
    {
        var ordersIndexes = new List<CatalogIndex>
        {
            new("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, KeyColumns: ["OrderId", "RevisionId"], IncludedColumns: []),
        };
        if (extraOrdersIndexes is not null)
        {
            ordersIndexes.AddRange(extraOrdersIndexes);
        }

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("RevisionId")], ordersIndexes));
        catalog.AddOrReplace(Table("dbo", "OrderLines", [Col("LineId"), Col("OrderId"), Col("RevisionId")]));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_OrderLines_Orders", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId"));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_OrderLines_Orders", "dbo.OrderLines", "RevisionId", "dbo.Orders", "RevisionId"));
        return catalog;
    }

    private static IReadOnlyList<PartialCompositeForeignKeyJoinFinding> Scan(string sql, DatabaseCatalog catalog)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var compositeForeignKeys = PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog);
        return PartialCompositeForeignKeyJoinScanner.Scan(result, catalog, compositeForeignKeys);
    }

    [Fact]
    public void BuildCompositeForeignKeys_ExcludesSingleColumnForeignKeys()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("CustomerId")]));
        catalog.AddOrReplace(Table("dbo", "Customers", [Col("CustomerId")]));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Single", "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId"));

        var composite = PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog);

        Assert.Empty(composite);
    }

    [Fact]
    public void BuildCompositeForeignKeys_KeepsCompositeForeignKeys()
    {
        var catalog = BuildCatalog();

        var composite = PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog);

        var fk = Assert.Single(composite);
        Assert.Equal("FK_OrderLines_Orders", fk.ConstraintName);
        Assert.Equal(2, fk.Pairs.Count);
    }

    [Fact]
    public void JoinOnOnlyOneOfTwoCompositeColumns_Fires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId;", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("FK_OrderLines_Orders", finding.ConstraintName);
        Assert.Single(finding.MatchedColumnPairs);
        Assert.Equal("OrderId", finding.MatchedColumnPairs[0].ParentColumnName);
        Assert.Single(finding.MissingColumnPairs);
        Assert.Equal("RevisionId", finding.MissingColumnPairs[0].ParentColumnName);
    }

    [Fact]
    public void JoinOnOnlyOneOfTwoCompositeColumns_WhereClauseUnsatisfiable_NeverFires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId WHERE ol.LineId = 1 AND ol.LineId = 2;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void CteSharesNameWithReferencedTable_JoinNeverFires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "WITH Orders AS (SELECT LineId AS OrderId FROM dbo.OrderLines) " +
            "SELECT 1 FROM dbo.OrderLines ol JOIN Orders o ON ol.OrderId = o.OrderId;",
            catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void JoinOnBothCompositeColumns_NeverFires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId AND ol.RevisionId = o.RevisionId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void MissingColumnCoveredSeparatelyInWhereClause_NeverFires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId WHERE ol.RevisionId = o.RevisionId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void MissingColumnMatchedAgainstAnUnrelatedThirdTable_StillFires()
    {
        var catalog = BuildCatalog();
        catalog.AddOrReplace(Table("dbo", "Audit", [Col("OrderId"), Col("RevisionId")]));
        var findings = Scan(
            """
            SELECT 1 FROM dbo.OrderLines ol
            JOIN dbo.Orders o ON ol.OrderId = o.OrderId
            JOIN dbo.Audit a ON ol.RevisionId = a.RevisionId AND ol.OrderId = a.OrderId;
            """, catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("RevisionId", finding.MissingColumnPairs[0].ParentColumnName);
    }

    [Fact]
    public void UsedColumnSubsetCoveredByItsOwnUniqueIndexOnReferencedSide_Suppressed()
    {
        var catalog = BuildCatalog(extraOrdersIndexes:
        [
            new CatalogIndex("UX_Orders_OrderId", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["OrderId"], IncludedColumns: []),
        ]);

        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void JoinMatchingNoneOfTheForeignKeyColumns_NeverFires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.LineId = o.OrderId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void LegacyCommaJoinOnOnlyOneColumn_Fires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol, dbo.Orders o WHERE ol.OrderId = o.OrderId;", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("RevisionId", finding.MissingColumnPairs[0].ParentColumnName);
    }

    [Fact]
    public void LegacyCommaJoinOnBothColumns_NeverFires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol, dbo.Orders o WHERE ol.OrderId = o.OrderId AND ol.RevisionId = o.RevisionId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateFromJoinOnOnlyOneColumn_Fires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "UPDATE ol SET ol.LineId = ol.LineId FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId;", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("RevisionId", finding.MissingColumnPairs[0].ParentColumnName);
    }

    [Fact]
    public void NoCompositeForeignKeysInCatalog_ScanShortCircuitsToEmpty()
    {
        var catalog = new DatabaseCatalog();
        var result = SqlScriptParser.ParseText("test.sql", "SELECT 1 FROM dbo.A a JOIN dbo.B b ON a.X = b.X;");
        Assert.False(result.HasErrors);

        var findings = PartialCompositeForeignKeyJoinScanner.Scan(result, catalog, PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog));

        Assert.Empty(findings);
    }

    [Fact]
    public void JoinAgainstUnrelatedThirdTable_NeverFires()
    {
        var catalog = BuildCatalog();
        catalog.AddOrReplace(Table("dbo", "Customers", [Col("OrderId")]));
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Customers c ON ol.OrderId = c.OrderId;", catalog);

        Assert.Empty(findings);
    }
}
