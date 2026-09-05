using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class MemoryOptimizedUnsupportedColumnTypeScannerTests
{
    private static IReadOnlyList<MemoryOptimizedUnsupportedColumnTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return MemoryOptimizedUnsupportedColumnTypeScanner.Scan(catalog);
    }

    [Theory]
    [InlineData("XML")]
    [InlineData("SQL_VARIANT")]
    [InlineData("TEXT")]
    [InlineData("NTEXT")]
    [InlineData("IMAGE")]
    [InlineData("ROWVERSION")]
    [InlineData("HIERARCHYID")]
    [InlineData("GEOMETRY")]
    [InlineData("GEOGRAPHY")]
    public void UnsupportedType_OnMemoryOptimizedTable_Fires(string type)
    {
        var findings = Scan(
            $"""
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag {type} NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Widgets", finding.TableQualifiedName);
        Assert.Equal("Tag", finding.ColumnName);
    }

    [Fact]
    public void SameUnsupportedType_OnOrdinaryTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY, Tag XML NULL);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void OrdinaryTypes_OnMemoryOptimizedTable_NeverFire()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Widgets (
                Id INT NOT NULL PRIMARY KEY NONCLUSTERED,
                Name NVARCHAR(100) NULL,
                Notes VARCHAR(MAX) NULL,
                Payload VARBINARY(MAX) NULL
            ) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MultipleOffendingColumns_OrderedByTableThenColumn()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.B (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Zeta XML NULL, Alpha SQL_VARIANT NULL) WITH (MEMORY_OPTIMIZED = ON);
            CREATE TABLE dbo.A (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Payload TEXT NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Equal(3, findings.Count);
        Assert.Equal(["dbo.A", "dbo.B", "dbo.B"], findings.Select(f => f.TableQualifiedName));
        Assert.Equal(["Payload", "Alpha", "Zeta"], findings.Select(f => f.ColumnName));
    }
}
