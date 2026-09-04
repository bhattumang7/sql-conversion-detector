using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ExternalTableUnsupportedColumnTypeScannerTests
{
    private static IReadOnlyList<ExternalTableUnsupportedColumnTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return ExternalTableUnsupportedColumnTypeScanner.Scan(result, catalog);
    }

    [Theory]
    [InlineData("SQL_VARIANT")]
    [InlineData("XML")]
    [InlineData("HIERARCHYID")]
    [InlineData("GEOMETRY")]
    [InlineData("GEOGRAPHY")]
    [InlineData("NTEXT")]
    [InlineData("TEXT")]
    [InlineData("IMAGE")]
    [InlineData("TIMESTAMP")]
    [InlineData("VARCHAR(MAX)")]
    [InlineData("NVARCHAR(MAX)")]
    [InlineData("VARBINARY(MAX)")]
    public void UnsupportedType_Fires(string dataType)
    {
        var findings = Scan($"""
            CREATE EXTERNAL TABLE dbo.Ext (Id INT NOT NULL, Payload {dataType} NULL)
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Ext", finding.TableQualifiedName);
        Assert.Equal("Payload", finding.ColumnName);
    }

    [Theory]
    [InlineData("INT")]
    [InlineData("VARCHAR(4000)")]
    [InlineData("NVARCHAR(4000)")]
    [InlineData("VARBINARY(8000)")]
    [InlineData("DATETIME2")]
    [InlineData("DECIMAL(18,2)")]
    public void SupportedType_DoesNotFire(string dataType)
    {
        var findings = Scan($"""
            CREATE EXTERNAL TABLE dbo.Ext (Id INT NOT NULL, Payload {dataType} NULL)
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MultipleUnsupportedColumns_FiresForEach()
    {
        var findings = Scan("""
            CREATE EXTERNAL TABLE dbo.Ext (Id INT NOT NULL, A XML NULL, B GEOGRAPHY NULL)
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt);
            """);

        Assert.Equal(2, findings.Count);
        Assert.Equal(["A", "B"], findings.Select(f => f.ColumnName).ToArray());
    }

    [Fact]
    public void CreateExternalTableAsSelect_WithUnresolvableSourceTypes_DoesNotFire()
    {
        var findings = Scan("""
            CREATE EXTERNAL TABLE dbo.Ext
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void CreateExternalTableAsSelect_WithUnsupportedSourceColumnType_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Src (Id INT NOT NULL, Notes XML NULL);
            CREATE EXTERNAL TABLE dbo.Ext
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Notes", finding.ColumnName);
    }

    [Fact]
    public void CreateExternalTableAsSelect_WithAliasedUnsupportedExpression_UsesAliasAsColumnName()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Src (Id INT NOT NULL, Notes NVARCHAR(4000) NULL);
            CREATE EXTERNAL TABLE dbo.Ext
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Wide = CAST(Notes AS NVARCHAR(MAX)) FROM dbo.Src;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Wide", finding.ColumnName);
    }

    [Fact]
    public void CreateExternalTableAsSelect_WithSupportedSourceColumnType_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Src (Id INT NOT NULL, Notes NVARCHAR(4000) NULL);
            CREATE EXTERNAL TABLE dbo.Ext
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void CreateExternalTableAsSelect_WithCte_ResolvesThroughCteRelation()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Src (Id INT NOT NULL, Notes XML NULL);
            CREATE EXTERNAL TABLE dbo.Ext
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS WITH Cte AS (SELECT Id, Notes FROM dbo.Src)
            SELECT Id, Notes FROM Cte;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Notes", finding.ColumnName);
    }

    [Fact]
    public void CreateExternalTableAsSelect_WithUnionArmProjectingUnsupportedType_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Src (Id INT NOT NULL, Notes NVARCHAR(4000) NULL);
            CREATE TABLE dbo.OtherSrc (Id INT NOT NULL, Notes XML NULL);
            CREATE EXTERNAL TABLE dbo.Ext
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src
            UNION ALL
            SELECT Id, Notes FROM dbo.OtherSrc;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("Notes", finding.ColumnName);
    }

    [Fact]
    public void CreateExternalTableAsSelect_WithAllUnionArmsSupported_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Src (Id INT NOT NULL, Notes NVARCHAR(4000) NULL);
            CREATE TABLE dbo.OtherSrc (Id INT NOT NULL, Notes NVARCHAR(4000) NULL);
            CREATE EXTERNAL TABLE dbo.Ext
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src
            UNION ALL
            SELECT Id, Notes FROM dbo.OtherSrc;
            """);

        Assert.Empty(findings);
    }
}
