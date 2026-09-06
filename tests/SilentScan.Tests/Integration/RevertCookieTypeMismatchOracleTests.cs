using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

public sealed class RevertCookieTypeMismatchOracleTests : OracleTestFixture
{
    private const int InvalidRevertDataTypeErrorNumber = 15533;

    protected override string DatabaseNameSeed => nameof(RevertCookieTypeMismatchOracleTests);

    protected override string Ddl => string.Empty;

    [Fact]
    public async Task NarrowerVarbinaryCookie_FailsWithInvalidRevertDataType()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            DECLARE @cookie varbinary(10);
            EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
            REVERT WITH COOKIE = @cookie;
            """));

        Assert.Equal(InvalidRevertDataTypeErrorNumber, ex.Number);
    }

    [Fact]
    public async Task ExactVarbinary100Cookie_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            DECLARE @cookie varbinary(100);
            EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
            REVERT WITH COOKIE = @cookie;
            """));

        Assert.Null(exception);
    }

    [Fact]
    public async Task JustBelowMinimumVarbinaryCookie_FailsWithInvalidRevertDataType()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            DECLARE @cookie varbinary(49);
            EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
            REVERT WITH COOKIE = @cookie;
            """));

        Assert.Equal(InvalidRevertDataTypeErrorNumber, ex.Number);
    }

    [Fact]
    public async Task MinimumVarbinaryCookie_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            DECLARE @cookie varbinary(50);
            EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
            REVERT WITH COOKIE = @cookie;
            """));

        Assert.Null(exception);
    }

    [Fact]
    public async Task WiderThanConventionalVarbinaryCookie_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            DECLARE @cookie varbinary(200);
            EXECUTE AS USER = 'dbo' WITH COOKIE INTO @cookie;
            REVERT WITH COOKIE = @cookie;
            """));

        Assert.Null(exception);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
