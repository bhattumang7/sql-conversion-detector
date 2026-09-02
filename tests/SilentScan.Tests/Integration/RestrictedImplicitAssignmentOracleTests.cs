using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

public sealed class RestrictedImplicitAssignmentOracleTests : OracleTestFixture
{
    private const int OperandTypeClashErrorNumber = 206;
    private const int ImplicitConversionNotAllowedErrorNumber = 257;

    protected override string DatabaseNameSeed => nameof(RestrictedImplicitAssignmentOracleTests);

    protected override string Ddl => string.Empty;

    [Fact]
    public async Task VariantVariableAssignedToXmlVariable_FailsWithOperandTypeClash()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            DECLARE @v sql_variant = 5;
            DECLARE @x xml;
            SET @x = @v;
            """));

        Assert.Equal(OperandTypeClashErrorNumber, ex.Number);
    }

    [Fact]
    public async Task XmlVariableAssignedToVariantVariable_FailsWithOperandTypeClash()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            DECLARE @x xml = '<a/>';
            DECLARE @v sql_variant;
            SET @v = @x;
            """));

        Assert.Equal(OperandTypeClashErrorNumber, ex.Number);
    }

    [Fact]
    public async Task VariantVariableAssignedToIntVariable_FailsWithImplicitConversionNotAllowed()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            DECLARE @v sql_variant = 5;
            DECLARE @i int;
            SET @i = @v;
            """));

        Assert.Equal(ImplicitConversionNotAllowedErrorNumber, ex.Number);
    }

    [Fact]
    public async Task XmlVariableAssignedToIntVariable_FailsWithOperandTypeClash()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            DECLARE @x xml = '<a/>';
            DECLARE @i int;
            SET @i = @x;
            """));

        Assert.Equal(OperandTypeClashErrorNumber, ex.Number);
    }

    [Fact]
    public async Task IntVariableAssignedToXmlVariable_FailsWithOperandTypeClash()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            DECLARE @i int = 5;
            DECLARE @x xml;
            SET @x = @i;
            """));

        Assert.Equal(OperandTypeClashErrorNumber, ex.Number);
    }

    [Fact]
    public async Task IntVariableAssignedToVariantVariable_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            DECLARE @i int = 5;
            DECLARE @v sql_variant;
            SET @v = @i;
            """));

        Assert.Null(exception);
    }

    [Fact]
    public async Task VarcharVariableAssignedToXmlVariable_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            DECLARE @s varchar(50) = '<a/>';
            DECLARE @x xml;
            SET @x = @s;
            """));

        Assert.Null(exception);
    }

    [Fact]
    public async Task VarbinaryVariableAssignedToXmlVariable_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            DECLARE @b varbinary(50) = 0x3C612F3E;
            DECLARE @x xml;
            SET @x = @b;
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
