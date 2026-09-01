using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class MemoryOptimizedSchemaOnlyDurabilityScannerTests
{
    private static IReadOnlyList<MemoryOptimizedSchemaOnlyDurabilityFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return MemoryOptimizedSchemaOnlyDurabilityScanner.Scan(catalog);
    }

    [Fact]
    public void SchemaOnlyDurability_OnMemoryOptimizedTable_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.SessionCache (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Payload NVARCHAR(100) NULL) WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.SessionCache", finding.TableQualifiedName);
    }

    [Fact]
    public void SchemaAndDataDurability_OnMemoryOptimizedTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.SessionCache (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Payload NVARCHAR(100) NULL) WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_AND_DATA);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void OmittedDurability_OnMemoryOptimizedTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.SessionCache (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Payload NVARCHAR(100) NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SchemaOnlyDurability_OnOrdinaryDiskBasedTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.SessionCache (Id INT NOT NULL PRIMARY KEY, Payload NVARCHAR(100) NULL);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MultipleTables_OnlySchemaOnlyOnesFire_OrderedByTable()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Beta (Id INT NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY);
            CREATE TABLE dbo.Alpha (Id INT NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY);
            CREATE TABLE dbo.Gamma (Id INT NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_AND_DATA);
            """);

        Assert.Equal(["dbo.Alpha", "dbo.Beta"], findings.Select(f => f.TableQualifiedName));
    }
}
