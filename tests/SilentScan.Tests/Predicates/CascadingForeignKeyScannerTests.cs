using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class CascadingForeignKeyScannerTests
{
    [Fact]
    public void DeleteCascade_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId", DeleteAction: ReferentialAction.Cascade));

        var findings = CascadingForeignKeyScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(ReferentialAction.Cascade, finding.DeleteAction);
        Assert.Equal(ReferentialAction.NoAction, finding.UpdateAction);
    }

    [Fact]
    public void UpdateSetNull_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId", UpdateAction: ReferentialAction.SetNull));

        var findings = CascadingForeignKeyScanner.Scan(catalog);

        Assert.Equal(ReferentialAction.SetNull, Assert.Single(findings).UpdateAction);
    }

    [Fact]
    public void NoAction_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId"));

        Assert.Empty(CascadingForeignKeyScanner.Scan(catalog));
    }

    [Fact]
    public void CompositeForeignKey_ReportedOncePerConstraint()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Composite", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId", DeleteAction: ReferentialAction.Cascade));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Composite", "dbo.OrderLines", "RevisionId", "dbo.Orders", "RevisionId", DeleteAction: ReferentialAction.Cascade));

        Assert.Single(CascadingForeignKeyScanner.Scan(catalog));
    }
}
