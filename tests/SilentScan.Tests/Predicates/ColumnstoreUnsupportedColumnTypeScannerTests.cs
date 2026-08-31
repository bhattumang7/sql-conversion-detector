using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ColumnstoreUnsupportedColumnTypeScannerTests
{
    private static IReadOnlyList<ColumnstoreUnsupportedColumnTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return ColumnstoreUnsupportedColumnTypeScanner.Scan(catalog);
    }

    [Fact]
    public void SqlVariantColumnOnClusteredColumnstoreTable_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Amount INT NOT NULL, LegacyTag SQL_VARIANT NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_Sales ON dbo.Sales;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Sales", finding.TableQualifiedName);
        Assert.Equal("LegacyTag", finding.ColumnName);
        Assert.Equal("SqlVariant", finding.TypeDisplay, ignoreCase: true);
        Assert.Equal("CCI_Sales", finding.IndexName);
    }

    [Fact]
    public void SqlVariantColumnNamedInNonclusteredColumnstoreList_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Amount INT NOT NULL, LegacyTag SQL_VARIANT NULL);
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Sales ON dbo.Sales (Amount, LegacyTag);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("LegacyTag", finding.ColumnName);
        Assert.Equal("NCCI_Sales", finding.IndexName);
    }

    [Fact]
    public void SqlVariantColumnOmittedFromNonclusteredColumnstoreList_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Amount INT NOT NULL, LegacyTag SQL_VARIANT NULL);
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Sales ON dbo.Sales (SaleId, Amount);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SqlVariantColumnOnRowstoreTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Amount INT NOT NULL, LegacyTag SQL_VARIANT NULL);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ColumnstoreTableWithNoSqlVariantColumn_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Amount INT NOT NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_Sales ON dbo.Sales;
            """);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("XML")]
    [InlineData("HIERARCHYID")]
    [InlineData("GEOMETRY")]
    [InlineData("GEOGRAPHY")]
    [InlineData("NTEXT")]
    [InlineData("TEXT")]
    [InlineData("IMAGE")]
    [InlineData("ROWVERSION")]
    public void UnsupportedTypeColumnOnClusteredColumnstoreTable_Fires(string typeName)
    {
        var findings = Scan(
            $"""
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Payload {typeName} NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_Sales ON dbo.Sales;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Payload", finding.ColumnName);
    }

    [Theory]
    [InlineData("VARCHAR(MAX)")]
    [InlineData("NVARCHAR(MAX)")]
    [InlineData("VARBINARY(MAX)")]
    public void MaxTypedColumnOnClusteredColumnstoreTable_NeverFires(string typeName)
    {
        var findings = Scan(
            $"""
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Payload {typeName} NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_Sales ON dbo.Sales;
            """);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("VARCHAR(MAX)")]
    [InlineData("NVARCHAR(MAX)")]
    [InlineData("VARBINARY(MAX)")]
    public void MaxTypedColumnNamedInNonclusteredColumnstoreList_Fires(string typeName)
    {
        var findings = Scan(
            $"""
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Amount INT NOT NULL, Payload {typeName} NULL);
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Sales ON dbo.Sales (Amount, Payload);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Payload", finding.ColumnName);
    }

    [Fact]
    public void MaxTypedColumnOmittedFromNonclusteredColumnstoreList_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Sales (SaleId INT NOT NULL, Amount INT NOT NULL, Payload VARCHAR(MAX) NULL);
            CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Sales ON dbo.Sales (SaleId, Amount);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MultipleOffendingColumns_OrderedByTableThenColumn()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.B (Zeta SQL_VARIANT NULL, Alpha SQL_VARIANT NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_B ON dbo.B;
            CREATE TABLE dbo.A (Payload SQL_VARIANT NULL);
            CREATE CLUSTERED COLUMNSTORE INDEX CCI_A ON dbo.A;
            """);

        Assert.Equal(3, findings.Count);
        Assert.Equal(["dbo.A", "dbo.B", "dbo.B"], findings.Select(f => f.TableQualifiedName));
        Assert.Equal(["Payload", "Alpha", "Zeta"], findings.Select(f => f.ColumnName));
    }
}
