using Microsoft.Data.SqlClient;
using SilentScan.Verify;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class StringAggResultTypeCapOracleTests
{
    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;

    private async Task<string> DescribeSystemTypeName(string selectSql)
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT system_type_name FROM sys.dm_exec_describe_first_result_set(N'" +
            selectSql.Replace("'", "''") + "', NULL, 0);";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task NonUnicodeOperandsWithNoMaxLeg_CapsAtVarchar8000()
    {
        var systemTypeName = await DescribeSystemTypeName(
            "SELECT STRING_AGG(CAST(x AS varchar(10)), ',') AS X FROM (VALUES ('a'), ('b')) AS t(x)");

        Assert.Equal("varchar(8000)", systemTypeName);
    }

    [Fact]
    public async Task UnicodeOperandsWithNoMaxLeg_CapsAtNvarchar4000()
    {
        var systemTypeName = await DescribeSystemTypeName(
            "SELECT STRING_AGG(CAST(x AS nvarchar(10)), N',') AS X FROM (VALUES (N'a'), (N'b')) AS t(x)");

        Assert.Equal("nvarchar(4000)", systemTypeName);
    }

    [Fact]
    public async Task MaxTypedValueOperand_IsNotCapped()
    {
        var systemTypeName = await DescribeSystemTypeName(
            "SELECT STRING_AGG(CAST(x AS varchar(max)), ',') AS X FROM (VALUES ('a'), ('b')) AS t(x)");

        Assert.Equal("varchar(max)", systemTypeName);
    }

    [Fact]
    public async Task MaxTypedSeparator_IsRejectedAtCompileTime()
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DECLARE @sep varchar(max) = ','; " +
            "SELECT STRING_AGG(CAST(x AS varchar(10)), @sep) AS X FROM (VALUES ('a'), ('b')) AS t(x);";

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());

        Assert.Equal(8734, exception.Number);
    }
}
