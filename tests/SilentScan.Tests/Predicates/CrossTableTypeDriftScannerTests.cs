using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md Tier 1 "Join-key and cross-object type/
/// collation mismatch": FK-linked cross-table type drift). <see cref="DatabaseCatalog.ForeignKeys"/>
/// is only ever populated by live mode (<see cref="SilentScan.Verify.Catalog.LiveCatalogReader"/>,
/// covered separately in the Oracle-tagged <c>LiveCatalogReaderTests</c>) - these tests build the
/// catalog directly to exercise the scanner's own comparison logic without needing the Docker
/// oracle for every case.
/// </summary>
public sealed class CrossTableTypeDriftScannerTests
{
    private static CatalogTable Table(string schema, string name, string columnName, SqlType type) =>
        new(schema, name, CatalogTableKind.Table,
            [new CatalogColumn(columnName, type, IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false)],
            [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    [Fact]
    public void GenuinelyDifferentCategories_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", "CustomerId", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))));
        catalog.AddOrReplace(Table("dbo", "Customers", "CustomerId", new SqlType(SqlTypeCategory.Int)));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId"));

        var findings = CrossTableTypeDriftScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("FK_Test", finding.ConstraintName);
        Assert.False(finding.CollationDiffers);
    }

    [Fact]
    public void SameCategorySameCollation_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", "CustomerCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))));
        catalog.AddOrReplace(Table("dbo", "Customers", "CustomerCode", new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.Orders", "CustomerCode", "dbo.Customers", "CustomerCode"));

        var findings = CrossTableTypeDriftScanner.Scan(catalog);

        // Length-only difference within the same category is not a conversion-seed concern -
        // VerdictClassifier's own same-category rule already treats this as sargable.
        Assert.Empty(findings);
    }

    [Fact]
    public void SameCategoryDifferentCollation_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", "CustomerCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))));
        catalog.AddOrReplace(Table("dbo", "Customers", "CustomerCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("French_CI_AS"))));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.Orders", "CustomerCode", "dbo.Customers", "CustomerCode"));

        var findings = CrossTableTypeDriftScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.True(finding.CollationDiffers);
    }

    [Fact]
    public void UnresolvedColumnType_NeverGuesses()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", "CustomerId", new SqlType(SqlTypeCategory.Int)));
        // No dbo.Customers table registered at all - the FK's referenced side is unresolvable.
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId"));

        var findings = CrossTableTypeDriftScanner.Scan(catalog);

        Assert.Empty(findings);
    }
}
