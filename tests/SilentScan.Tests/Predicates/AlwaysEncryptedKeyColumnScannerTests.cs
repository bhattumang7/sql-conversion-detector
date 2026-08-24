using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class AlwaysEncryptedKeyColumnScannerTests
{
    private static IReadOnlyList<AlwaysEncryptedKeyColumnFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return AlwaysEncryptedKeyColumnScanner.Scan(catalog);
    }

    private const string NonEnclaveKeySetup = """
        CREATE COLUMN MASTER KEY CMK1
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/0000000000000000000000000000000000000000');
        GO
        CREATE COLUMN ENCRYPTION KEY CEK1
        WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        """;

    private const string EnclaveKeySetup = """
        CREATE COLUMN MASTER KEY CMK1
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/0000000000000000000000000000000000000000', ENCLAVE_COMPUTATIONS (SIGNATURE = 0x01000000));
        GO
        CREATE COLUMN ENCRYPTION KEY CEK1
        WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        """;

    [Fact]
    public void RandomizedColumn_AsIndexKey_WithNonEnclaveKey_Fires()
    {
        var findings = Scan(
            NonEnclaveKeySetup + "\n" + """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Ssn        INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
            );
            GO
            CREATE INDEX IX_Ssn ON dbo.Customer(Ssn);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customer", finding.TableQualifiedName);
        Assert.Equal("Ssn", finding.ColumnName);
        Assert.Equal("IX_Ssn", finding.ObjectName);
        Assert.Equal(AlwaysEncryptedKeyColumnKind.Index, finding.Kind);
    }

    [Fact]
    public void RandomizedColumn_AsPrimaryKey_WithNonEnclaveKey_Fires()
    {
        var findings = Scan(
            NonEnclaveKeySetup + "\n" + """
            CREATE TABLE dbo.Customer
            (
                Ssn INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
                    PRIMARY KEY
            );
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(AlwaysEncryptedKeyColumnKind.PrimaryKey, finding.Kind);
    }

    [Fact]
    public void RandomizedColumn_AsUniqueConstraint_WithNonEnclaveKey_Fires()
    {
        var findings = Scan(
            NonEnclaveKeySetup + "\n" + """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Ssn        INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
                    UNIQUE
            );
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(AlwaysEncryptedKeyColumnKind.UniqueConstraint, finding.Kind);
    }

    [Fact]
    public void RandomizedColumn_AsIndexKey_WithEnclaveEnabledKey_NeverFires()
    {
        var findings = Scan(
            EnclaveKeySetup + "\n" + """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Ssn        INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
            );
            GO
            CREATE INDEX IX_Ssn ON dbo.Customer(Ssn);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DeterministicColumn_AsIndexKey_WithNonEnclaveKey_NeverFires()
    {
        var findings = Scan(
            NonEnclaveKeySetup + "\n" + """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Ssn        INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
            );
            GO
            CREATE INDEX IX_Ssn ON dbo.Customer(Ssn);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void RandomizedColumn_NotAKeyColumn_WithNonEnclaveKey_NeverFires()
    {
        var findings = Scan(
            NonEnclaveKeySetup + "\n" + """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Ssn        INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
            );
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void RandomizedColumn_AsIndexKey_WithUnresolvedEncryptionKey_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Ssn        INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_NotInCorpus, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
            );
            GO
            CREATE INDEX IX_Ssn ON dbo.Customer(Ssn);
            """);

        Assert.Empty(findings);
    }
}
