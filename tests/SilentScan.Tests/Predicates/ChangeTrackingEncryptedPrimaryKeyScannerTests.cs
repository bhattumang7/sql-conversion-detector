using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ChangeTrackingEncryptedPrimaryKeyScannerTests
{
    private const string CekDdl = """
        CREATE COLUMN MASTER KEY TestCmk
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/4444444444444444444444444444444444444444');
        GO
        CREATE COLUMN ENCRYPTION KEY TestCek
        WITH VALUES (COLUMN_MASTER_KEY = TestCmk, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        """;

    private static IReadOnlyList<ChangeTrackingEncryptedPrimaryKeyFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{CekDdl}\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return ChangeTrackingEncryptedPrimaryKeyScanner.Scan(result, catalog);
    }

    [Fact]
    public void EnableChangeTracking_OnTableWithEncryptedPrimaryKeyColumn_Fires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Customer
            (
                Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                     ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = TestCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                     NOT NULL PRIMARY KEY,
                Name NVARCHAR(100) NULL
            );

            ALTER TABLE dbo.Customer ENABLE CHANGE_TRACKING;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customer", finding.TableQualifiedName);
        Assert.Equal("Ssn", finding.ColumnName);
    }

    [Fact]
    public void EnableChangeTracking_OnTableWithEncryptedNonPrimaryKeyColumn_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Customer
            (
                Id   INT NOT NULL PRIMARY KEY,
                Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                     ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = TestCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                     NOT NULL,
                Name NVARCHAR(100) NULL
            );

            ALTER TABLE dbo.Customer ENABLE CHANGE_TRACKING;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void EnableChangeTracking_OnTableWithUnencryptedPrimaryKey_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Customer (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NULL);

            ALTER TABLE dbo.Customer ENABLE CHANGE_TRACKING;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DisableChangeTracking_OnTableWithEncryptedPrimaryKey_DoesNotFire()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Customer
            (
                Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                     ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = TestCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                     NOT NULL PRIMARY KEY,
                Name NVARCHAR(100) NULL
            );

            ALTER TABLE dbo.Customer DISABLE CHANGE_TRACKING;
            """);

        Assert.Empty(findings);
    }
}
