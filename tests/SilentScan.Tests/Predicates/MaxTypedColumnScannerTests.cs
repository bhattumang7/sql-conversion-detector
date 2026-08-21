using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters":
/// "a string/binary column declared MAX-typed can never be an index key column at all") - no AST
/// walking, no predicate site needed. A structural fact SQL Server itself enforces at CREATE
/// INDEX time (Msg 1919: "Column ... in table ... is of a type that is invalid for use as a key
/// column in an index"), cited directly rather than through a third party since it's a plain
/// catalog-metadata property, not a plan-behavior claim needing an oracle probe.
/// </summary>
public sealed class MaxTypedColumnScannerTests
{
    private static IReadOnlyList<MaxTypedColumnFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return MaxTypedColumnScanner.Scan(catalog);
    }

    [Theory]
    [InlineData("VARCHAR(MAX)", "varchar(max)")]
    [InlineData("NVARCHAR(MAX)", "nvarchar(max)")]
    [InlineData("VARBINARY(MAX)", "varbinary(max)")]
    public void MaxTypedColumn_Fires(string declaredType, string expectedTypeDisplay)
    {
        var findings = Scan($"CREATE TABLE dbo.Documents (Body {declaredType} NOT NULL);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Documents", finding.TableQualifiedName);
        Assert.Equal("Body", finding.ColumnName);
        Assert.Equal(expectedTypeDisplay, finding.TypeDisplay, ignoreCase: true);
        Assert.Equal(NonIndexableColumnFindingKind.MaxLength, finding.Kind);
    }

    [Theory]
    [InlineData("VARCHAR(8000)")]
    [InlineData("NVARCHAR(4000)")]
    [InlineData("VARBINARY(8000)")]
    [InlineData("INT")]
    [InlineData("DATETIME2(7)")]
    public void BoundedLengthColumn_NeverFires(string declaredType)
    {
        var findings = Scan($"CREATE TABLE dbo.Documents (Body {declaredType} NOT NULL);");

        Assert.Empty(findings);
    }

    /// <summary>
    /// Oracle-confirmed directly (Docker SQL Server 2022): TEXT/NTEXT/IMAGE are rejected as an
    /// index KEY column (Msg 1919, same as MAX-typed) AND, unlike MAX-typed columns, also
    /// rejected as a nonclustered index's INCLUDE column (Msg 1999) - a genuinely stronger, and
    /// genuinely distinct, restriction from the MAX-typed-column fact.
    /// </summary>
    [Theory]
    [InlineData("TEXT", "text")]
    [InlineData("NTEXT", "ntext")]
    [InlineData("IMAGE", "image")]
    public void LegacyLargeObjectColumn_Fires(string declaredType, string expectedTypeDisplay)
    {
        var findings = Scan($"CREATE TABLE dbo.Documents (Body {declaredType} NULL);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Documents", finding.TableQualifiedName);
        Assert.Equal("Body", finding.ColumnName);
        Assert.Equal(expectedTypeDisplay, finding.TypeDisplay, ignoreCase: true);
        Assert.Equal(NonIndexableColumnFindingKind.LegacyLargeObject, finding.Kind);
    }

    [Fact]
    public void MultipleMaxTypedColumns_OrderedByTableThenColumn()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.B (Zeta NVARCHAR(MAX) NOT NULL, Alpha VARCHAR(MAX) NOT NULL);
            CREATE TABLE dbo.A (Payload VARBINARY(MAX) NOT NULL);
            """);

        Assert.Equal(3, findings.Count);
        Assert.Equal(["dbo.A", "dbo.B", "dbo.B"], findings.Select(f => f.TableQualifiedName));
        Assert.Equal(["Payload", "Alpha", "Zeta"], findings.Select(f => f.ColumnName));
    }
}
