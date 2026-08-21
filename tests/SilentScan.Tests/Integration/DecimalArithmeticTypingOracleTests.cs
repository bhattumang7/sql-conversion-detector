using Microsoft.Data.SqlClient;
using SilentScan.Verify;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DecimalArithmeticTypingOracleTests
{
    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;

    private async Task<(int Precision, int Scale)> DescribeExpressionType(string expressionSql)
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT precision, scale FROM sys.dm_exec_describe_first_result_set(N'SELECT " +
            expressionSql.Replace("'", "''") + " AS X', NULL, 0);";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ((byte)reader["precision"], (byte)reader["scale"]);
    }

    [Theory]
    [InlineData("CAST(1 AS DECIMAL(5,2)) + CAST(1 AS DECIMAL(5,2))", 6, 2)]
    [InlineData("CAST(1 AS DECIMAL(5,2)) - CAST(1 AS DECIMAL(5,2))", 6, 2)]
    [InlineData("CAST(1 AS DECIMAL(5,2)) * CAST(1 AS DECIMAL(5,2))", 11, 4)]
    [InlineData("CAST(1 AS DECIMAL(5,2)) / CAST(1 AS DECIMAL(5,2))", 13, 8)]
    [InlineData("CAST(1 AS DECIMAL(9,2)) + CAST(1 AS DECIMAL(5,4))", 12, 4)]
    [InlineData("CAST(1 AS DECIMAL(9,2)) * CAST(1 AS DECIMAL(9,4))", 19, 6)]
    [InlineData("CAST(1 AS DECIMAL(9,2)) / CAST(1 AS DECIMAL(5,4))", 19, 8)]
    public async Task DecimalWithDecimal_MatchesExactFormula(string expressionSql, int expectedPrecision, int expectedScale)
    {
        var (precision, scale) = await DescribeExpressionType(expressionSql);

        Assert.Equal(expectedPrecision, precision);
        Assert.Equal(expectedScale, scale);
    }

    [Theory]
    [InlineData("CAST(1 AS TINYINT) + CAST(1 AS DECIMAL(5,2))", 6, 2)]
    [InlineData("CAST(1 AS SMALLINT) + CAST(1 AS DECIMAL(5,2))", 8, 2)]
    [InlineData("CAST(1 AS INT) + CAST(1 AS DECIMAL(5,2))", 13, 2)]
    [InlineData("CAST(1 AS DECIMAL(5,2)) + CAST(1 AS INT)", 13, 2)]
    [InlineData("CAST(1 AS BIGINT) + CAST(1 AS DECIMAL(5,2))", 22, 2)]
    [InlineData("CAST(1 AS SMALLMONEY) + CAST(1 AS DECIMAL(5,2))", 11, 4)]
    [InlineData("CAST(1 AS MONEY) + CAST(1 AS DECIMAL(5,2))", 20, 4)]
    public async Task IntegerOrMoneyOperandNormalizesToItsDecimalEquivalent(string expressionSql, int expectedPrecision, int expectedScale)
    {
        var (precision, scale) = await DescribeExpressionType(expressionSql);

        Assert.Equal(expectedPrecision, precision);
        Assert.Equal(expectedScale, scale);
    }

    [Theory]
    [InlineData("CAST(1 AS DECIMAL(38,0)) + CAST(1 AS DECIMAL(38,0))", 38, 0)]
    [InlineData("CAST(1 AS DECIMAL(38,37)) + CAST(1 AS DECIMAL(38,37))", 38, 37)]
    [InlineData("CAST(1 AS DECIMAL(38,0)) + CAST(1 AS DECIMAL(38,38))", 38, 0)]
    [InlineData("CAST(1 AS DECIMAL(38,38)) + CAST(1 AS DECIMAL(38,38))", 38, 38)]
    [InlineData("CAST(1 AS DECIMAL(30,0)) + CAST(1 AS DECIMAL(10,10))", 38, 8)]
    public async Task AddSubtractOverflow_ScaleReductionHasNoFloor(string expressionSql, int expectedPrecision, int expectedScale)
    {
        var (precision, scale) = await DescribeExpressionType(expressionSql);

        Assert.Equal(expectedPrecision, precision);
        Assert.Equal(expectedScale, scale);
    }

    [Theory]
    [InlineData("CAST(1 AS DECIMAL(38,0)) * CAST(1 AS DECIMAL(38,0))", 38, 0)]
    [InlineData("CAST(1 AS DECIMAL(38,10)) * CAST(1 AS DECIMAL(38,0))", 38, 6)]
    [InlineData("CAST(1 AS DECIMAL(20,10)) * CAST(1 AS DECIMAL(20,10))", 38, 17)]
    [InlineData("CAST(1 AS DECIMAL(38,0)) / CAST(1 AS DECIMAL(38,38))", 38, 6)]
    [InlineData("CAST(1 AS DECIMAL(38,38)) / CAST(1 AS DECIMAL(1,0))", 38, 38)]
    public async Task MultiplyDivideOverflow_ScaleFloorsAtSix(string expressionSql, int expectedPrecision, int expectedScale)
    {
        var (precision, scale) = await DescribeExpressionType(expressionSql);

        Assert.Equal(expectedPrecision, precision);
        Assert.Equal(expectedScale, scale);
    }

    [Theory]
    [InlineData("CAST(1 AS FLOAT) + CAST(1 AS DECIMAL(5,2))")]
    [InlineData("CAST(1 AS INT) + CAST(1 AS BIGINT)")]
    public async Task NonExactOrPureIntegerArithmetic_NeverProducesADecimalCappedByThisFormula(string expressionSql)
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT system_type_name FROM sys.dm_exec_describe_first_result_set(N'SELECT " +
            expressionSql.Replace("'", "''") + " AS X', NULL, 0);";
        var systemTypeName = (string)(await command.ExecuteScalarAsync())!;

        Assert.DoesNotContain("decimal", systemTypeName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("numeric", systemTypeName, StringComparison.OrdinalIgnoreCase);
    }
}
