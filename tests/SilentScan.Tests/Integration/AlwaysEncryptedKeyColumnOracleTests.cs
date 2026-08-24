using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

public sealed class AlwaysEncryptedKeyColumnOracleTests : OracleTestFixture
{
    private const int NonEnclaveKeyColumnErrorNumber = 33573;

    protected override string DatabaseNameSeed => nameof(AlwaysEncryptedKeyColumnOracleTests);

    protected override string Ddl => """
        CREATE COLUMN MASTER KEY CMK_NoEnclave
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/1111111111111111111111111111111111111111');
        GO
        CREATE COLUMN ENCRYPTION KEY CEK_NoEnclave
        WITH VALUES (COLUMN_MASTER_KEY = CMK_NoEnclave, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        CREATE COLUMN MASTER KEY CMK_Enclave
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/2222222222222222222222222222222222222222', ENCLAVE_COMPUTATIONS (SIGNATURE = 0x01000000));
        GO
        CREATE COLUMN ENCRYPTION KEY CEK_Enclave
        WITH VALUES (COLUMN_MASTER_KEY = CMK_Enclave, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        CREATE TABLE dbo.Customer
        (
            CustomerId          INT NOT NULL PRIMARY KEY,
            RandomizedNoEnclave INT
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_NoEnclave, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            DeterministicNoEnclave INT
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_NoEnclave, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            RandomizedEnclave   INT
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_Enclave, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
        );
        """;

    [Fact]
    public async Task IndexOnRandomizedColumn_WithNonEnclaveKey_FailsWithNonEnclaveKeyColumnError()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync("CREATE INDEX IX_RandomizedNoEnclave ON dbo.Customer(RandomizedNoEnclave);"));

        Assert.Equal(NonEnclaveKeyColumnErrorNumber, ex.Number);
    }

    [Fact]
    public async Task IndexOnDeterministicColumn_WithNonEnclaveKey_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() =>
            ExecuteAsync("CREATE INDEX IX_DeterministicNoEnclave ON dbo.Customer(DeterministicNoEnclave);"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task IndexOnRandomizedColumn_WithEnclaveEnabledKey_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() =>
            ExecuteAsync("CREATE INDEX IX_RandomizedEnclave ON dbo.Customer(RandomizedEnclave);"));

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
