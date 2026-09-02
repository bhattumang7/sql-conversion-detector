using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class MemoryOptimizedUtf8CollationScannerTests
{
    private static IReadOnlyList<MemoryOptimizedUtf8CollationFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return MemoryOptimizedUtf8CollationScanner.Scan(catalog);
    }

    [Theory]
    [InlineData("VARCHAR(50)")]
    [InlineData("CHAR(10)")]
    public void Utf8Collation_OnMemoryOptimizedTable_Fires(string type)
    {
        var findings = Scan(
            $"""
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag {type} COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Widgets", finding.TableQualifiedName);
        Assert.Equal("Tag", finding.ColumnName);
        Assert.Equal("Latin1_General_100_CI_AS_SC_UTF8", finding.CollationName);
    }

    [Fact]
    public void Utf8Collation_OnOrdinaryTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY, Tag VARCHAR(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NonUtf8Collation_OnMemoryOptimizedTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag VARCHAR(50) COLLATE Latin1_General_100_CI_AS NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void Utf8Collation_OnNvarcharColumn_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag NVARCHAR(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Empty(findings);
    }
}
