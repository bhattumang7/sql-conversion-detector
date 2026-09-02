using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class SchemaWithRejectedTypeOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(SchemaWithRejectedTypeOracleTests);

    protected override string Ddl => "CREATE TABLE dbo.Placeholder (Id INT NOT NULL PRIMARY KEY);";

    private static IReadOnlyList<SchemaWithRejectedTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return SchemaWithRejectedTypeScanner.Scan(result);
    }

    private async Task<SqlException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task OpenXmlWithGeometryColumn_FailsAtExecutionWithMsg6632_AndScannerFlagsIt()
    {
        const string Sql = """
            DECLARE @doc INT;
            EXEC sp_xml_preparedocument @doc OUTPUT, N'<Root><Item a="1"/></Root>';
            SELECT * FROM OPENXML(@doc, '/Root/Item', 1) WITH (a geometry);
            EXEC sp_xml_removedocument @doc;
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(6632, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal(SchemaWithRejectedTypeKind.OpenXmlClrType, finding.Kind);
        Assert.Equal("a", finding.ColumnName);
    }

    [Fact]
    public async Task OpenXmlWithTextColumn_CompilesAndReturnsRows_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            DECLARE @doc INT;
            EXEC sp_xml_preparedocument @doc OUTPUT, N'<Root><Item a="1"/></Root>';
            SELECT * FROM OPENXML(@doc, '/Root/Item', 1) WITH (a text);
            EXEC sp_xml_removedocument @doc;
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await command.ExecuteNonQueryAsync();

        Assert.Empty(Scan(Sql));
    }

    [Fact]
    public async Task OpenRowsetWithSqlVariantColumn_FailsToCompileWithMsg13801_AndScannerFlagsIt()
    {
        const string Sql = "SELECT * FROM OPENROWSET(BULK 'nonexistent_file.csv', FORMAT = 'CSV') WITH (a INT, b SQL_VARIANT) AS x;";

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(13801, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal(SchemaWithRejectedTypeKind.OpenRowsetLegacyType, finding.Kind);
        Assert.Equal("b", finding.ColumnName);
    }

    [Fact]
    public async Task OpenRowsetWithGeometryColumn_FailsToCompileWithMsg13802_AndScannerFlagsIt()
    {
        const string Sql = "SELECT * FROM OPENROWSET(BULK 'nonexistent_file.csv', FORMAT = 'CSV') WITH (a INT, b geometry) AS x;";

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(13802, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal(SchemaWithRejectedTypeKind.OpenRowsetClrType, finding.Kind);
    }

    [Fact]
    public async Task OpenRowsetWithXmlColumn_FailsToCompileWithMsg13829_AndScannerFlagsIt()
    {
        const string Sql = "SELECT * FROM OPENROWSET(BULK 'nonexistent_file.csv', FORMAT = 'CSV') WITH (a INT, b XML) AS x;";

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(13829, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal(SchemaWithRejectedTypeKind.OpenRowsetXml, finding.Kind);
    }

    [Fact]
    public async Task OpenRowsetWithNvarcharMaxColumn_NeverFailsWithSchemaGateMessage_AndScannerDoesNotFlagIt()
    {
        const string Sql = "SELECT * FROM OPENROWSET(BULK 'nonexistent_file.csv', FORMAT = 'CSV') WITH (a INT, b NVARCHAR(MAX)) AS x;";

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.NotEqual(13801, exception.Number);
        Assert.NotEqual(13802, exception.Number);
        Assert.NotEqual(13829, exception.Number);

        Assert.Empty(Scan(Sql));
    }
}
