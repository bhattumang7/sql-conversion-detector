using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

public sealed class AlwaysEncryptedUnsupportedColumnOracleTests : OracleTestFixture
{
    private const int UnsupportedDataTypeErrorNumber = 33280;
    private const int IdentityEncryptedErrorNumber = 2749;

    protected override string DatabaseNameSeed => nameof(AlwaysEncryptedUnsupportedColumnOracleTests);

    protected override string Ddl => """
        CREATE COLUMN MASTER KEY CMK1
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/3333333333333333333333333333333333333333');
        GO
        CREATE COLUMN ENCRYPTION KEY CEK1
        WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        """;

    [Fact]
    public async Task CreateTable_EncryptedXmlColumn_FailsWithUnsupportedDataTypeError()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            CREATE TABLE dbo.T_Xml
            (
                Payload XML
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
            );
            """));

        Assert.Equal(UnsupportedDataTypeErrorNumber, ex.Number);
    }

    [Fact]
    public async Task CreateTable_EncryptedTimestampColumn_FailsWithUnsupportedDataTypeError()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            CREATE TABLE dbo.T_Timestamp
            (
                Payload TIMESTAMP
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
            );
            """));

        Assert.Equal(UnsupportedDataTypeErrorNumber, ex.Number);
    }

    [Fact]
    public async Task CreateTable_EncryptedMaxLengthColumn_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            CREATE TABLE dbo.T_MaxLength
            (
                Payload NVARCHAR(MAX)
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
            );
            """));

        Assert.Null(exception);
    }

    [Fact]
    public async Task CreateTable_EncryptedIdentityColumn_FailsWithIdentityEncryptedError()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            CREATE TABLE dbo.T_Identity
            (
                CustomerId INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                    IDENTITY
            );
            """));

        Assert.Equal(IdentityEncryptedErrorNumber, ex.Number);
    }

    [Fact]
    public async Task CreateTable_UnencryptedIdentityColumn_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            CREATE TABLE dbo.T_PlainIdentity
            (
                CustomerId INT IDENTITY NOT NULL PRIMARY KEY
            );
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
