using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class MemoryOptimizedUnsupportedIndexOptionScannerTests
{
    private static IReadOnlyList<MemoryOptimizedUnsupportedIndexOptionFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return MemoryOptimizedUnsupportedIndexOptionScanner.Scan(catalog);
    }

    [Fact]
    public void IncludedColumns_OnMemoryOptimizedTable_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Widgets (
                Id INT NOT NULL PRIMARY KEY NONCLUSTERED,
                Amount INT NULL,
                Note NVARCHAR(50) NULL,
                INDEX IX_Amount NONCLUSTERED (Amount) INCLUDE (Note)
            ) WITH (MEMORY_OPTIMIZED = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(MemoryOptimizedUnsupportedIndexOptionKind.IncludedColumns, finding.Kind);
    }

    [Fact]
    public void PlainNonclusteredIndex_OnMemoryOptimizedTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Widgets (
                Id INT NOT NULL PRIMARY KEY NONCLUSTERED,
                Amount INT NULL,
                INDEX IX_Amount NONCLUSTERED (Amount)
            ) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ClusteredRowstoreIndex_OnMemoryOptimizedTable_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable(
            "dbo", "Widgets", CatalogTableKind.Table,
            [new CatalogColumn("Id", null, IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false)],
            [new CatalogIndex("PK_Widgets", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true)],
            "test.sql", 1, IsMemoryOptimized: true));

        var findings = MemoryOptimizedUnsupportedIndexOptionScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("PK_Widgets", finding.IndexName);
        Assert.Equal(MemoryOptimizedUnsupportedIndexOptionKind.ClusteredIndex, finding.Kind);
    }

    [Fact]
    public void ClusteredColumnstoreIndex_OnMemoryOptimizedTable_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable(
            "dbo", "Widgets", CatalogTableKind.Table,
            [new CatalogColumn("Id", null, IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false)],
            [
                new CatalogIndex("PK_Widgets", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: false),
                new CatalogIndex("CCI_Widgets", CatalogIndexKind.Index, IsUnique: false, [], [], IsClustered: true, IsColumnstore: true),
            ],
            "test.sql", 1, IsMemoryOptimized: true));

        var findings = MemoryOptimizedUnsupportedIndexOptionScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void ClusteredRowstoreIndex_OnOrdinaryTable_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(new CatalogTable(
            "dbo", "Widgets", CatalogTableKind.Table,
            [new CatalogColumn("Id", null, IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false)],
            [new CatalogIndex("PK_Widgets", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true)],
            "test.sql", 1, IsMemoryOptimized: false));

        var findings = MemoryOptimizedUnsupportedIndexOptionScanner.Scan(catalog);

        Assert.Empty(findings);
    }
}
