using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class AlwaysEncryptedUnsupportedColumnScannerTests
{
    private static IReadOnlyList<AlwaysEncryptedUnsupportedColumnFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return AlwaysEncryptedUnsupportedColumnScanner.Scan(catalog);
    }

    private const string KeySetup = """
        CREATE COLUMN MASTER KEY CMK1
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/0000000000000000000000000000000000000000');
        GO
        CREATE COLUMN ENCRYPTION KEY CEK1
        WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        """;

    [Theory]
    [InlineData("XML")]
    [InlineData("TIMESTAMP")]
    [InlineData("IMAGE")]
    [InlineData("TEXT")]
    [InlineData("NTEXT")]
    [InlineData("SQL_VARIANT")]
    [InlineData("HIERARCHYID")]
    [InlineData("GEOGRAPHY")]
    [InlineData("GEOMETRY")]
    [InlineData("JSON")]
    public void UnsupportedDataType_Encrypted_Fires(string typeName)
    {
        var findings = Scan(
            KeySetup + "\n" + $$"""
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Payload    {{typeName}}
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
            );
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customer", finding.TableQualifiedName);
        Assert.Equal("Payload", finding.ColumnName);
        Assert.Equal(AlwaysEncryptedUnsupportedColumnKind.UnsupportedDataType, finding.Kind);
    }

    [Theory]
    [InlineData("VARCHAR(MAX)")]
    [InlineData("NVARCHAR(MAX)")]
    [InlineData("VARBINARY(MAX)")]
    [InlineData("DECIMAL(18,2)")]
    public void SupportedDataType_Encrypted_NeverFires(string typeName)
    {
        var findings = Scan(
            KeySetup + "\n" + $$"""
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Payload    {{typeName}}
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
            );
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void UnsupportedDataType_Unencrypted_NeverFires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Payload    XML
            );
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void IdentityColumn_Encrypted_Fires()
    {
        var findings = Scan(
            KeySetup + "\n" + """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                    IDENTITY
            );
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customer", finding.TableQualifiedName);
        Assert.Equal("CustomerId", finding.ColumnName);
        Assert.Equal(AlwaysEncryptedUnsupportedColumnKind.IdentityColumn, finding.Kind);
    }

    [Fact]
    public void IdentityColumn_Unencrypted_NegativeControl_NeverFires()
    {
        var findings = Scan("""
            CREATE TABLE dbo.Customer
            (
                CustomerId INT IDENTITY NOT NULL PRIMARY KEY
            );
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NonIdentityColumn_Encrypted_NegativeControl_NeverFires()
    {
        var findings = Scan(
            KeySetup + "\n" + """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Ssn        INT
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
            );
            """);

        Assert.Empty(findings);
    }
}
