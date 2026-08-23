using Microsoft.Data.SqlClient;
using SilentScan.Verify;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LiteralTypingOracleTests
{
    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;

    private async Task<(string Type, int Precision, int Scale)> DescribeLiteralType(string literalExpression)
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT system_type_name, precision, scale FROM sys.dm_exec_describe_first_result_set(N'SELECT " +
            literalExpression + " AS X', NULL, 0);";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var systemTypeName = (string)reader["system_type_name"];
        var baseType = systemTypeName.Split('(')[0];
        return (baseType, (byte)reader["precision"], (byte)reader["scale"]);
    }

    [Fact]
    public async Task ScientificNotationLiteral_TypesAsFloat()
    {
        var (type, precision, _) = await DescribeLiteralType("1.5e10");

        Assert.Equal("float", type);
        Assert.Equal(53, precision);
    }

    [Fact]
    public async Task IntMaxValuePlusOneIntegerValuedLiteral_TypesAsDecimalNotBigInt()
    {
        var (type, precision, scale) = await DescribeLiteralType("2147483648");

        Assert.Equal("numeric", type);
        Assert.Equal(10, precision);
        Assert.Equal(0, scale);
    }

    [Fact]
    public async Task BigIntMaxValueIntegerValuedLiteral_TypesAsDecimalNotBigInt()
    {
        var (type, precision, scale) = await DescribeLiteralType("9223372036854775807");

        Assert.Equal("numeric", type);
        Assert.Equal(19, precision);
        Assert.Equal(0, scale);
    }

    [Fact]
    public async Task InRangeIntegerLiteral_StillTypesAsInt()
    {
        var (type, _, _) = await DescribeLiteralType("2147483647");

        Assert.Equal("int", type);
    }

    private async Task<int> EmptyStringLiteralMaxLengthBytes(string literalExpression)
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT SQL_VARIANT_PROPERTY(CAST({literalExpression} AS sql_variant), 'MaxLength');";
        var result = await command.ExecuteScalarAsync();
        return (int)result!;
    }

    [Fact]
    public async Task EmptyStringLiteral_TypesWithLengthOneNotZero()
    {
        Assert.Equal(1, await EmptyStringLiteralMaxLengthBytes("''"));
    }

    [Fact]
    public async Task EmptyNationalStringLiteral_TypesWithLengthOneNotZero()
    {
        Assert.Equal(2, await EmptyStringLiteralMaxLengthBytes("N''"));
    }
}
