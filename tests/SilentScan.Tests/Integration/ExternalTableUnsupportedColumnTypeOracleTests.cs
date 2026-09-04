using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
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
        CREATE TABLE dbo.Src (Id INT NOT NULL, Payload XML NULL, Notes NVARCHAR(4000) NULL);
        CREATE TABLE dbo.OtherSrc (Id INT NOT NULL, Payload XML NULL, Notes NVARCHAR(4000) NULL);
        """;

    private static IReadOnlyList<ExternalTableUnsupportedColumnTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return ExternalTableUnsupportedColumnTypeScanner.Scan(result, catalog);
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

    [Fact]
    public async Task CetasWithUnsupportedSourceColumnType_FailsToDeployWithMsg15877_AndScannerFlagsIt()
    {
        const string Sql = """
            CREATE EXTERNAL TABLE dbo.CetasExt
            WITH (LOCATION = '/y/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Payload FROM dbo.Src;
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(15877, exception.Number);

        const string ScannerSql = """
            CREATE TABLE dbo.Src (Id INT NOT NULL, Payload XML NULL, Notes NVARCHAR(4000) NULL);

            CREATE EXTERNAL TABLE dbo.CetasExt
            WITH (LOCATION = '/y/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Payload FROM dbo.Src;
            """;

        var finding = Assert.Single(Scan(ScannerSql));
        Assert.Equal("Payload", finding.ColumnName);
    }

    [Fact]
    public async Task CetasWithSupportedSourceColumnType_NeverFailsWithTheTypeGateMessage_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            CREATE EXTERNAL TABLE dbo.CetasExt
            WITH (LOCATION = '/y/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src;
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.NotEqual(15877, exception.Number);

        const string ScannerSql = """
            CREATE TABLE dbo.Src (Id INT NOT NULL, Payload XML NULL, Notes NVARCHAR(4000) NULL);

            CREATE EXTERNAL TABLE dbo.CetasExt
            WITH (LOCATION = '/y/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src;
            """;

        Assert.Empty(Scan(ScannerSql));
    }

    [Fact]
    public async Task CetasWithUnsupportedTypeInUnionArm_FailsToDeployWithMsg15877_AndScannerFlagsIt()
    {
        const string Sql = """
            CREATE EXTERNAL TABLE dbo.CetasUnionExt
            WITH (LOCATION = '/z/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src
            UNION ALL
            SELECT Id, Payload AS Notes FROM dbo.OtherSrc;
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(15877, exception.Number);

        const string ScannerSql = """
            CREATE TABLE dbo.Src (Id INT NOT NULL, Payload XML NULL, Notes NVARCHAR(4000) NULL);
            CREATE TABLE dbo.OtherSrc (Id INT NOT NULL, Payload XML NULL, Notes NVARCHAR(4000) NULL);

            CREATE EXTERNAL TABLE dbo.CetasUnionExt
            WITH (LOCATION = '/z/', DATA_SOURCE = ExtSrc, FILE_FORMAT = ExtFmt)
            AS SELECT Id, Notes FROM dbo.Src
            UNION ALL
            SELECT Id, Payload AS Notes FROM dbo.OtherSrc;
            """;

        var finding = Assert.Single(Scan(ScannerSql));
        Assert.Equal("Notes", finding.ColumnName);
    }
}
