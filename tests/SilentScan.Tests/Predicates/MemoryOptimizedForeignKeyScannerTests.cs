using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class MemoryOptimizedForeignKeyScannerTests
{
    private static CatalogTable Table(string schema, string name, bool isMemoryOptimized) => new(
        schema, name, CatalogTableKind.Table,
        [new CatalogColumn("Id", null, IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false)],
        [],
        "test.sql", 1, IsMemoryOptimized: isMemoryOptimized);

    [Fact]
    public void ForeignKey_SpanningMemoryOptimizedAndDiskBasedTables_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "OrderLines", isMemoryOptimized: true));
        catalog.AddOrReplace(Table("dbo", "Orders", isMemoryOptimized: false));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId"));

        var findings = MemoryOptimizedForeignKeyScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(MemoryOptimizedForeignKeyFindingKind.CrossStorageForeignKey, finding.Kind);
        Assert.Equal("FK_Test", finding.ConstraintName);
    }

    [Fact]
    public void ForeignKey_SpanningDiskBasedAndMemoryOptimizedTables_ReverseDirection_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "OrderLines", isMemoryOptimized: false));
        catalog.AddOrReplace(Table("dbo", "Orders", isMemoryOptimized: true));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId"));

        var findings = MemoryOptimizedForeignKeyScanner.Scan(catalog);

        Assert.Equal(MemoryOptimizedForeignKeyFindingKind.CrossStorageForeignKey, Assert.Single(findings).Kind);
    }

    [Fact]
    public void ForeignKey_BetweenTwoDiskBasedTables_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "OrderLines", isMemoryOptimized: false));
        catalog.AddOrReplace(Table("dbo", "Orders", isMemoryOptimized: false));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId", DeleteAction: ReferentialAction.Cascade));

        Assert.Empty(MemoryOptimizedForeignKeyScanner.Scan(catalog));
    }

    [Fact]
    public void ForeignKey_BetweenTwoMemoryOptimizedTables_WithNoAction_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "OrderLines", isMemoryOptimized: true));
        catalog.AddOrReplace(Table("dbo", "Orders", isMemoryOptimized: true));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId"));

        Assert.Empty(MemoryOptimizedForeignKeyScanner.Scan(catalog));
    }

    [Fact]
    public void ForeignKey_BetweenTwoMemoryOptimizedTables_WithDeleteCascade_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "OrderLines", isMemoryOptimized: true));
        catalog.AddOrReplace(Table("dbo", "Orders", isMemoryOptimized: true));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId", DeleteAction: ReferentialAction.Cascade));

        var findings = MemoryOptimizedForeignKeyScanner.Scan(catalog);

        Assert.Equal(MemoryOptimizedForeignKeyFindingKind.ReferentialAction, Assert.Single(findings).Kind);
    }

    [Fact]
    public void ForeignKey_BetweenTwoMemoryOptimizedTables_WithUpdateSetNull_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "OrderLines", isMemoryOptimized: true));
        catalog.AddOrReplace(Table("dbo", "Orders", isMemoryOptimized: true));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId", UpdateAction: ReferentialAction.SetNull));

        var findings = MemoryOptimizedForeignKeyScanner.Scan(catalog);

        Assert.Equal(MemoryOptimizedForeignKeyFindingKind.ReferentialAction, Assert.Single(findings).Kind);
    }

    [Fact]
    public void CompositeForeignKey_ReportedOncePerConstraint()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "OrderLines", isMemoryOptimized: true));
        catalog.AddOrReplace(Table("dbo", "Orders", isMemoryOptimized: true));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Composite", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId", DeleteAction: ReferentialAction.Cascade));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Composite", "dbo.OrderLines", "RevisionId", "dbo.Orders", "RevisionId", DeleteAction: ReferentialAction.Cascade));

        Assert.Single(MemoryOptimizedForeignKeyScanner.Scan(catalog));
    }

    [Fact]
    public void ForeignKey_ReferencingUnresolvedTable_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "OrderLines", isMemoryOptimized: true));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Test", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId"));

        Assert.Empty(MemoryOptimizedForeignKeyScanner.Scan(catalog));
    }
}
