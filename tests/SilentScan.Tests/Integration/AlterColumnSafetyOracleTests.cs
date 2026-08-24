using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

public sealed class AlterColumnSafetyOracleTests : OracleTestFixture
{
    private const int ArithmeticOverflowErrorNumber = 8115;
    private const int ImplicitConversionNotAllowedErrorNumber = 257;

    protected override string DatabaseNameSeed => nameof(AlterColumnSafetyOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Invoice (InvoiceId INT NOT NULL PRIMARY KEY, Total DECIMAL(10, 4) NOT NULL);
        INSERT INTO dbo.Invoice (InvoiceId, Total) VALUES (1, 12345.6789);

        CREATE TABLE dbo.Event (EventId INT NOT NULL PRIMARY KEY, StartTime TIME(6) NOT NULL);
        INSERT INTO dbo.Event (EventId, StartTime) VALUES (1, '12:34:56.123456');

        CREATE TABLE dbo.Document (DocumentId INT NOT NULL PRIMARY KEY, Payload VARCHAR(50) NOT NULL);
        INSERT INTO dbo.Document (DocumentId, Payload) VALUES (1, 'hello');

        CREATE TABLE dbo.BinaryDocument (DocumentId INT NOT NULL PRIMARY KEY, Payload VARBINARY(50) NOT NULL);
        INSERT INTO dbo.BinaryDocument (DocumentId, Payload) VALUES (1, 0x48656C6C6F);
        """;

    [Fact]
    public async Task NarrowingPrecisionBelowExistingValue_FailsWithArithmeticOverflow()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("ALTER TABLE dbo.Invoice ALTER COLUMN Total DECIMAL(6, 4);"));

        Assert.Equal(ArithmeticOverflowErrorNumber, ex.Number);
    }

    [Fact]
    public async Task NarrowingScaleWhereValueStillFits_NegativeControl_SucceedsAndTruncates()
    {
        var exception = await Record.ExceptionAsync(() =>
            ExecuteAsync("ALTER TABLE dbo.Event ALTER COLUMN StartTime TIME(2);"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task VarcharToVarbinary_FailsWithNoImplicitConversion()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("ALTER TABLE dbo.Document ALTER COLUMN Payload VARBINARY(50);"));

        Assert.Equal(ImplicitConversionNotAllowedErrorNumber, ex.Number);
    }

    [Fact]
    public async Task VarbinaryToVarchar_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() =>
            ExecuteAsync("ALTER TABLE dbo.BinaryDocument ALTER COLUMN Payload VARCHAR(50);"));

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
