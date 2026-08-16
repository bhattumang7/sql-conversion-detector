using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md "Schema-scan UDF and computed-column findings":
/// "non-persisted computed column, independent of whether it references a UDF") - a plain
/// sys.computed_columns.is_persisted / DDL-PERSISTED-keyword read, no AST walking of query sites,
/// no oracle needed (recomputation-on-every-read is definitionally true for a non-persisted
/// computed column, the same way MaxTypedColumnScanner's own structural facts need none).
/// </summary>
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
        // A persisted computed column has already paid its recompute cost at write time,
        // regardless of whether it's also indexed - indexing is orthogonal to this rule.
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
