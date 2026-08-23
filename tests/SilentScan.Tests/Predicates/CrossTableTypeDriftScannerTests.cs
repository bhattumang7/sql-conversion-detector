using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

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
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId"));

        var findings = CrossTableTypeDriftScanner.Scan(catalog);

        Assert.Empty(findings);
    }
}
