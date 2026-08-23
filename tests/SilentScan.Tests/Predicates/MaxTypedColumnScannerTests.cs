using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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
