using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings": "Untrusted (WITH NOCHECK) FK/
/// CHECK constraints". <see cref="DatabaseCatalog.ForeignKeys"/>/<see cref="DatabaseCatalog.CheckConstraints"/>
/// are only ever populated by live mode - these tests build the catalog directly, the same
/// pattern <c>CrossTableTypeDriftScannerTests</c> already uses for the same reason.
/// </summary>
public sealed class UntrustedConstraintScannerTests
{
    private static CatalogTable Table(string schema, string name) =>
        new(schema, name, CatalogTableKind.Table, [], [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    [Fact]
    public void UntrustedForeignKey_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders"));
        catalog.AddOrReplace(Table("dbo", "Customers"));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId", IsNotTrusted: true));

        var findings = UntrustedConstraintScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(UntrustedConstraintFindingKind.ForeignKey, finding.Kind);
        Assert.Equal("FK_Test", finding.ConstraintName);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
    }

    [Fact]
    public void TrustedForeignKey_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId", IsNotTrusted: false));

        Assert.Empty(UntrustedConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void UntrustedButDisabledForeignKey_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId", IsNotTrusted: true, IsDisabled: true));

        Assert.Empty(UntrustedConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void CompositeForeignKey_ReportedOncePerConstraintNotPerColumnPair()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Composite", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId", IsNotTrusted: true));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Composite", "dbo.OrderLines", "RevisionId", "dbo.Orders", "RevisionId", IsNotTrusted: true));

        Assert.Single(UntrustedConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void UntrustedCheckConstraint_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders"));
        catalog.AddCheckConstraint(new CatalogCheckConstraint("CK_Orders_Amount", "dbo.Orders", IsNotTrusted: true, IsDisabled: false));

        var findings = UntrustedConstraintScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(UntrustedConstraintFindingKind.CheckConstraint, finding.Kind);
        Assert.Equal("CK_Orders_Amount", finding.ConstraintName);
    }

    [Fact]
    public void TrustedCheckConstraint_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddCheckConstraint(new CatalogCheckConstraint("CK_Orders_Amount", "dbo.Orders", IsNotTrusted: false, IsDisabled: false));

        Assert.Empty(UntrustedConstraintScanner.Scan(catalog));
    }
}
