using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Oracle-confirms the specific claim <see cref="Predicates.AlwaysEncryptedOrderByFinding"/> rests
/// on: an ORDER BY clause referencing an Always Encrypted column never compiles, for BOTH
/// DETERMINISTIC and RANDOMIZED encryption types, from a plain (non-Always-Encrypted-enabled)
/// connection - the same connection shape a real application's non-AE-aware batch tooling would
/// use. Asserts the specific engine error (Msg 33277, "Encryption scheme mismatch"), not merely
/// "an exception was thrown" (test-check.md point 6, "right artifact") - a wrong/unrelated
/// exception (a connection failure, a syntax error from a typo) must not be mistaken for this
/// claim being confirmed. The same-run negative control (ORDER BY on the plain, non-encrypted
/// column succeeds with no exception at all) proves the failure is specific to the encrypted
/// column, not a property of the connection/session/schema generally (test-check.md point 5).
/// </summary>
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
    public async Task OrderByPlainColumn_NegativeControl_Succeeds()
    {
        // Same schema, same connection, same run - only the target column differs. Proves the
        // two failures above are specific to the encrypted column, not e.g. a schema/deployment
        // problem that would make any ORDER BY fail on this database.
        await ExecuteAsync("SELECT CustomerId FROM dbo.Customer ORDER BY PlainName;");
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
