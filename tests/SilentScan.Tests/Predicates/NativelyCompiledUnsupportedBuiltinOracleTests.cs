using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class NativelyCompiledUnsupportedBuiltinOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(NativelyCompiledUnsupportedBuiltinOracleTests);

    protected override string Ddl =>
        """
        DECLARE @dataDir NVARCHAR(260) = (
            SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('/', REVERSE(physical_name)) + 1)
            FROM sys.master_files WHERE database_id = DB_ID() AND file_id = 1);
        DECLARE @sql NVARCHAR(MAX) = N'
            ALTER DATABASE CURRENT ADD FILEGROUP MemoryOptimizedFg CONTAINS MEMORY_OPTIMIZED_DATA;
            ALTER DATABASE CURRENT ADD FILE (name=''MemoryOptimizedFile'', filename=''' + @dataDir + N'memopt_nc_oracle'') TO FILEGROUP MemoryOptimizedFg;';
        EXEC sp_executesql @sql;

        CREATE TABLE dbo.Codes (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Name NVARCHAR(50) NOT NULL) WITH (MEMORY_OPTIMIZED = ON);
        """;

    private static IReadOnlyList<NativelyCompiledUnsupportedBuiltinFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return NativelyCompiledUnsupportedBuiltinScanner.Scan(result);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqlException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task StringAgg_InNativelyCompiledProcedure_DeploysCleanly_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            CREATE PROCEDURE dbo.SummarizeCodes
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @names NVARCHAR(100);
                SELECT @names = STRING_AGG(Name, N',') FROM dbo.Codes;
            END;
            """;

        await ExecuteAsync(Sql);

        Assert.Empty(Scan(Sql));
    }

    [Fact]
    public async Task Upper_InNativelyCompiledProcedure_StillFailsWithMsg10794_AndScannerFlagsIt()
    {
        const string Sql = """
            CREATE PROCEDURE dbo.NormalizeCodes
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @name NVARCHAR(50);
                SELECT TOP 1 @name = UPPER(Name) FROM dbo.Codes;
            END;
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(10794, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal("UPPER", finding.FunctionName);
    }
}
