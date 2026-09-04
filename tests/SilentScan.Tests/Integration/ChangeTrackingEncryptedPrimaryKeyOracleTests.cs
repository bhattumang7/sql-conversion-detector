using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ChangeTrackingEncryptedPrimaryKeyOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ChangeTrackingEncryptedPrimaryKeyOracleTests);

    protected override string Ddl => """
        ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
        GO
        CREATE COLUMN MASTER KEY CtCmk
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/3333333333333333333333333333333333333333');
        GO
        CREATE COLUMN ENCRYPTION KEY CtCek
        WITH VALUES (COLUMN_MASTER_KEY = CtCmk, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        CREATE TABLE dbo.EncryptedPkCustomer
        (
            Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                 ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CtCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                 NOT NULL PRIMARY KEY,
            Name NVARCHAR(100) NULL
        );
        GO
        CREATE TABLE dbo.EncryptedNonPkCustomer
        (
            Id   INT NOT NULL PRIMARY KEY,
            Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                 ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CtCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                 NOT NULL,
            Name NVARCHAR(100) NULL
        );
        """;

    private static IReadOnlyList<ChangeTrackingEncryptedPrimaryKeyFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return ChangeTrackingEncryptedPrimaryKeyScanner.Scan(result, catalog);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task EnableChangeTracking_OnTableWithEncryptedPrimaryKey_FailsWithMsg22118_AndScannerFlagsIt()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER TABLE dbo.EncryptedPkCustomer ENABLE CHANGE_TRACKING;"));

        Assert.Equal(22118, exception.Number);

        const string ScannerSql = """
            CREATE TABLE dbo.EncryptedPkCustomer
            (
                Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                     ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CtCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                     NOT NULL PRIMARY KEY,
                Name NVARCHAR(100) NULL
            );

            ALTER TABLE dbo.EncryptedPkCustomer ENABLE CHANGE_TRACKING;
            """;

        var finding = Assert.Single(Scan(ScannerSql));
        Assert.Equal("dbo.EncryptedPkCustomer", finding.TableQualifiedName);
        Assert.Equal("Ssn", finding.ColumnName);
    }

    [Fact]
    public async Task EnableChangeTracking_OnTableWithEncryptedNonPrimaryKeyColumn_Succeeds_AndScannerDoesNotFlagIt()
    {
        var exception = await Record.ExceptionAsync(
            () => ExecuteAsync("ALTER TABLE dbo.EncryptedNonPkCustomer ENABLE CHANGE_TRACKING;"));

        Assert.Null(exception);

        const string ScannerSql = """
            CREATE TABLE dbo.EncryptedNonPkCustomer
            (
                Id   INT NOT NULL PRIMARY KEY,
                Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                     ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CtCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                     NOT NULL,
                Name NVARCHAR(100) NULL
            );

            ALTER TABLE dbo.EncryptedNonPkCustomer ENABLE CHANGE_TRACKING;
            """;

        Assert.Empty(Scan(ScannerSql));
    }
}
