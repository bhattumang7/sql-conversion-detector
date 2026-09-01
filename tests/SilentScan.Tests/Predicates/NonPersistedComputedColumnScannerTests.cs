using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NonPersistedComputedColumnScannerTests
{
    private static IReadOnlyList<NonPersistedComputedColumnFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return NonPersistedComputedColumnScanner.Scan(catalog);
    }

    [Fact]
    public void NonPersistedComputedColumn_Fires()
    {
        var findings = Scan("CREATE TABLE dbo.Orders (Qty INT NOT NULL, Price MONEY NOT NULL, Total AS (Qty * Price));");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal("Total", finding.ColumnName);
        Assert.Contains("Qty", finding.DefinitionText);
        Assert.False(finding.IsCoveredByIndex);
    }

    [Fact]
    public void NonPersistedComputedColumn_CoveredByIndexKeyColumn_StillFiresButFlagsCoverage()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (Qty INT NOT NULL, Price MONEY NOT NULL, Total AS (Qty * Price));
            CREATE INDEX IX_Orders_Total ON dbo.Orders (Total);
            """);

        var finding = Assert.Single(findings);
        Assert.True(finding.IsCoveredByIndex);
    }

    [Fact]
    public void NonPersistedComputedColumn_CoveredByIndexIncludedColumn_StillFiresButFlagsCoverage()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (Qty INT NOT NULL, Price MONEY NOT NULL, Total AS (Qty * Price));
            CREATE INDEX IX_Orders_Qty ON dbo.Orders (Qty) INCLUDE (Total);
            """);

        var finding = Assert.Single(findings);
        Assert.True(finding.IsCoveredByIndex);
    }

    [Fact]
    public void NonPersistedComputedColumn_FilteredIndexOnly_NotTreatedAsCovered()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (Qty INT NOT NULL, Price MONEY NOT NULL, Total AS (Qty * Price));
            CREATE INDEX IX_Orders_Total ON dbo.Orders (Total) WHERE Total > 0;
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.IsCoveredByIndex);
    }

    [Fact]
    public void NonPersistedComputedColumn_DisabledIndexOnly_NotTreatedAsCovered()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (Qty INT NOT NULL, Price MONEY NOT NULL, Total AS (Qty * Price));
            CREATE INDEX IX_Orders_Total ON dbo.Orders (Total);
            ALTER INDEX IX_Orders_Total ON dbo.Orders DISABLE;
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.IsCoveredByIndex);
    }

    [Fact]
    public void PersistedComputedColumn_NeverFires()
    {
        var findings = Scan("CREATE TABLE dbo.Orders (Qty INT NOT NULL, Price MONEY NOT NULL, Total AS (Qty * Price) PERSISTED);");

        Assert.Empty(findings);
    }

    [Fact]
    public void OrdinaryColumn_NeverFires()
    {
        var findings = Scan("CREATE TABLE dbo.Orders (Qty INT NOT NULL);");

        Assert.Empty(findings);
    }

    [Fact]
    public void PersistedAndIndexedComputedColumn_NeverFires()
    {

        var findings = Scan(
            """
            CREATE TABLE dbo.Orders (Qty INT NOT NULL, Price MONEY NOT NULL, Total AS (Qty * Price) PERSISTED);
            CREATE INDEX IX_Orders_Total ON dbo.Orders (Total);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MultipleNonPersistedComputedColumns_OrderedByTableThenColumn()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.B (Zeta INT NOT NULL, Total AS (Zeta + 1), Half AS (Zeta / 2));
            CREATE TABLE dbo.A (X INT NOT NULL, Y AS (X + 1));
            """);

        Assert.Equal(3, findings.Count);
        Assert.Equal(["dbo.A", "dbo.B", "dbo.B"], findings.Select(f => f.TableQualifiedName));
        Assert.Equal(["Y", "Half", "Total"], findings.Select(f => f.ColumnName));
    }
}
