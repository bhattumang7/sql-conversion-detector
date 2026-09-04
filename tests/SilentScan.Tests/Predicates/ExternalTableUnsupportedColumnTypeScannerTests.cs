using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ExternalTableUnsupportedColumnTypeScannerTests
{
    private static IReadOnlyList<ExternalTableUnsupportedColumnTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return ExternalTableUnsupportedColumnTypeScanner.Scan(result);
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
    public void CreateExternalTableAsSelect_DoesNotFire()
    {
        var findings = Scan("""
            CREATE EXTERNAL TABLE dbo.Ext
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src;
            """);

        Assert.Empty(findings);
    }
}
