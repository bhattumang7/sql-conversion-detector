using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ExternalTableUnsupportedColumnTypeOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ExternalTableUnsupportedColumnTypeOracleTests);

    protected override string Ddl => """
        CREATE MASTER KEY ENCRYPTION BY PASSWORD = 'ExternalTableOracle_Test1234!';
        CREATE DATABASE SCOPED CREDENTIAL ExtCred WITH IDENTITY = 'user', SECRET = 'pw';
        CREATE EXTERNAL DATA SOURCE ExtSrc WITH (LOCATION = 'hdfs://namenode:8020', TYPE = HADOOP);
        CREATE EXTERNAL FILE FORMAT ExtFmt WITH (FORMAT_TYPE = DELIMITEDTEXT);
        """;

    private static IReadOnlyList<ExternalTableUnsupportedColumnTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return ExternalTableUnsupportedColumnTypeScanner.Scan(result);
    }

    private async Task<SqlException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("XML")]
    [InlineData("GEOGRAPHY")]
    [InlineData("NVARCHAR(MAX)")]
    public async Task UnsupportedType_FailsToDeployWithMsg46518_AndScannerFlagsIt(string dataType)
    {
        var sql = $"""
            CREATE EXTERNAL TABLE dbo.Ext (Id INT NOT NULL, Payload {dataType} NULL)
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt);
            """;

        var exception = await ExecuteExpectingFailureAsync(sql);
        Assert.Equal(46518, exception.Number);

        var finding = Assert.Single(Scan(sql));
        Assert.Equal("Payload", finding.ColumnName);
    }

    [Fact]
    public async Task SupportedType_NeverFailsWithTheTypeGateMessage_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            CREATE EXTERNAL TABLE dbo.Ext (Id INT NOT NULL, Payload NVARCHAR(4000) NULL)
            WITH (LOCATION = '/x/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt);
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.NotEqual(46518, exception.Number);

        Assert.Empty(Scan(Sql));
    }
}
