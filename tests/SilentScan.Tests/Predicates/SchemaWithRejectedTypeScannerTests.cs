using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class SchemaWithRejectedTypeScannerTests
{
    private static IReadOnlyList<SchemaWithRejectedTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return SchemaWithRejectedTypeScanner.Scan(result);
    }

    [Fact]
    public void OpenXml_WithGeometryColumn_Fires()
    {
        var findings = Scan("""
            DECLARE @doc INT;
            SELECT * FROM OPENXML(@doc, '/Root/Item', 1) WITH (a geometry);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SchemaWithRejectedTypeKind.OpenXmlClrType, finding.Kind);
        Assert.Equal("a", finding.ColumnName);
    }

    [Fact]
    public void OpenXml_WithGeographyOrHierarchyIdColumn_Fires()
    {
        var findings = Scan("""
            DECLARE @doc INT;
            SELECT * FROM OPENXML(@doc, '/Root/Item', 1) WITH (a geography, b hierarchyid);
            """);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(SchemaWithRejectedTypeKind.OpenXmlClrType, f.Kind));
    }

    [Theory]
    [InlineData("sql_variant")]
    [InlineData("text")]
    [InlineData("ntext")]
    [InlineData("image")]
    [InlineData("xml")]
    [InlineData("varchar(50)")]
    public void OpenXml_WithNonClrType_DoesNotFire(string dataType)
    {
        var findings = Scan($"""
            DECLARE @doc INT;
            SELECT * FROM OPENXML(@doc, '/Root/Item', 1) WITH (a {dataType});
            """);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("sql_variant")]
    [InlineData("text")]
    [InlineData("ntext")]
    [InlineData("image")]
    public void OpenRowsetBulkWith_LegacyType_Fires(string dataType)
    {
        var findings = Scan($"SELECT * FROM OPENROWSET(BULK 'file.csv', FORMAT = 'CSV') WITH (a INT, b {dataType}) AS x;");

        var finding = Assert.Single(findings);
        Assert.Equal(SchemaWithRejectedTypeKind.OpenRowsetLegacyType, finding.Kind);
        Assert.Equal("b", finding.ColumnName);
    }

    [Theory]
    [InlineData("geometry")]
    [InlineData("geography")]
    [InlineData("hierarchyid")]
    public void OpenRowsetBulkWith_ClrType_Fires(string dataType)
    {
        var findings = Scan($"SELECT * FROM OPENROWSET(BULK 'file.csv', FORMAT = 'CSV') WITH (a INT, b {dataType}) AS x;");

        var finding = Assert.Single(findings);
        Assert.Equal(SchemaWithRejectedTypeKind.OpenRowsetClrType, finding.Kind);
    }

    [Fact]
    public void OpenRowsetBulkWith_XmlType_Fires()
    {
        var findings = Scan("SELECT * FROM OPENROWSET(BULK 'file.csv', FORMAT = 'CSV') WITH (a INT, b XML) AS x;");

        var finding = Assert.Single(findings);
        Assert.Equal(SchemaWithRejectedTypeKind.OpenRowsetXml, finding.Kind);
    }

    [Theory]
    [InlineData("varchar(max)")]
    [InlineData("nvarchar(max)")]
    [InlineData("int")]
    [InlineData("datetime2")]
    public void OpenRowsetBulkWith_SupportedType_DoesNotFire(string dataType)
    {
        var findings = Scan($"SELECT * FROM OPENROWSET(BULK 'file.csv', FORMAT = 'CSV') WITH (a INT, b {dataType}) AS x;");

        Assert.Empty(findings);
    }

    [Fact]
    public void OpenRowsetBulkWith_MultipleRejectedColumns_FiresOncePerColumn()
    {
        var findings = Scan("SELECT * FROM OPENROWSET(BULK 'file.csv', FORMAT = 'CSV') WITH (a SQL_VARIANT, b geometry, c XML) AS x;");

        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, f => f.Kind == SchemaWithRejectedTypeKind.OpenRowsetLegacyType && f.ColumnName == "a");
        Assert.Contains(findings, f => f.Kind == SchemaWithRejectedTypeKind.OpenRowsetClrType && f.ColumnName == "b");
        Assert.Contains(findings, f => f.Kind == SchemaWithRejectedTypeKind.OpenRowsetXml && f.ColumnName == "c");
    }
}
