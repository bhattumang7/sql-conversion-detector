using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

public sealed class AlwaysEncryptedOrderByOracleTests : OracleTestFixture
{
    private const int EncryptionSchemeMismatchErrorNumber = 33277;

    protected override string DatabaseNameSeed => nameof(AlwaysEncryptedOrderByOracleTests);

    protected override string Ddl => """
        CREATE COLUMN MASTER KEY CMK1
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/0000000000000000000000000000000000000000');
        GO
        CREATE COLUMN ENCRYPTION KEY CEK1
        WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        CREATE TABLE dbo.Customer
        (
            CustomerId INT NOT NULL PRIMARY KEY,
            Ssn        CHAR(9) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            Notes      NVARCHAR(200) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            PlainName  NVARCHAR(100) NOT NULL
        );
        """;

    [Fact]
    public async Task OrderByDeterministicColumn_FailsWithEncryptionSchemeMismatch()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("SELECT CustomerId FROM dbo.Customer ORDER BY Ssn;"));

        Assert.Equal(EncryptionSchemeMismatchErrorNumber, ex.Number);
    }

    [Fact]
    public async Task OrderByRandomizedColumn_FailsWithEncryptionSchemeMismatch()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("SELECT CustomerId FROM dbo.Customer ORDER BY Notes;"));

        Assert.Equal(EncryptionSchemeMismatchErrorNumber, ex.Number);
    }

    [Fact]
    public async Task OrderByOrdinalPositionOfDeterministicColumn_FailsWithEncryptionSchemeMismatch()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("SELECT CustomerId, Ssn FROM dbo.Customer ORDER BY 2;"));

        Assert.Equal(EncryptionSchemeMismatchErrorNumber, ex.Number);
    }

    [Fact]
    public async Task OrderByPlainColumn_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("SELECT CustomerId FROM dbo.Customer ORDER BY PlainName;"));

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
