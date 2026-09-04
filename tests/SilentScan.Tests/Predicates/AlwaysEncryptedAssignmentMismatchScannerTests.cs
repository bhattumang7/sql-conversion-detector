using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class AlwaysEncryptedAssignmentMismatchScannerTests
{
    private const string BaseDdl = """
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
            SsnCopy    CHAR(9) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            PlainName  NVARCHAR(100) NOT NULL
        );
        """;

    private static IReadOnlyList<AlwaysEncryptedAssignmentMismatchFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{BaseDdl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return AlwaysEncryptedAssignmentMismatchScanner.Scan(result, catalog);
    }

    [Fact]
    public void UpdateSet_LiteralIntoEncryptedColumn_Fires()
    {
        var findings = Scan("UPDATE dbo.Customer SET Ssn = '123456789' WHERE CustomerId = 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(AlwaysEncryptedAssignmentMismatchKind.LiteralSource, finding.Kind);
        Assert.Equal("dbo.Customer", finding.TargetTableQualifiedName);
        Assert.Equal("Ssn", finding.TargetColumnName);
    }

    [Fact]
    public void UpdateSet_NullIntoEncryptedColumn_NeverFires()
    {
        var findings = Scan("UPDATE dbo.Customer SET Ssn = NULL WHERE CustomerId = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateSet_LiteralIntoPlainColumn_NeverFires()
    {
        var findings = Scan("UPDATE dbo.Customer SET PlainName = 'x' WHERE CustomerId = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateSet_ParameterIntoEncryptedColumn_NeverFires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.SetSsn @ssn CHAR(9) AS UPDATE dbo.Customer SET Ssn = @ssn WHERE CustomerId = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateSet_BetweenDifferentEncryptionTypes_Fires()
    {
        var findings = Scan("UPDATE dbo.Customer SET Notes = Ssn WHERE CustomerId = 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(AlwaysEncryptedAssignmentMismatchKind.EncryptionStateMismatch, finding.Kind);
        Assert.Equal("Notes", finding.TargetColumnName);
        Assert.Equal("Ssn", finding.SourceColumnName);
    }

    [Fact]
    public void UpdateSet_EncryptedIntoPlainColumn_Fires()
    {
        var findings = Scan("UPDATE dbo.Customer SET PlainName = Ssn WHERE CustomerId = 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(AlwaysEncryptedAssignmentMismatchKind.EncryptionStateMismatch, finding.Kind);
    }

    [Fact]
    public void UpdateSet_PlainIntoEncryptedColumn_Fires()
    {
        var findings = Scan("UPDATE dbo.Customer SET Ssn = PlainName WHERE CustomerId = 1;");

        Assert.Single(findings);
    }

    [Fact]
    public void UpdateSet_BetweenSameEncryptionTypeAndKey_NeverFires()
    {
        var findings = Scan("UPDATE dbo.Customer SET SsnCopy = Ssn WHERE CustomerId = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateSet_BetweenPlainColumns_NeverFires()
    {
        var findings = Scan("UPDATE dbo.Customer SET PlainName = PlainName WHERE CustomerId = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void InsertValues_LiteralIntoEncryptedColumn_Fires()
    {
        var findings = Scan("INSERT INTO dbo.Customer (CustomerId, Ssn, Notes, SsnCopy, PlainName) VALUES (1, '123456789', N'x', '123456789', 'x');");

        Assert.Equal(3, findings.Count);
        Assert.All(findings, f => Assert.Equal(AlwaysEncryptedAssignmentMismatchKind.LiteralSource, f.Kind));
    }

    [Fact]
    public void InsertValues_NullIntoEncryptedColumn_NeverFires()
    {
        var findings = Scan("INSERT INTO dbo.Customer (CustomerId, Ssn) VALUES (1, NULL);");

        Assert.Empty(findings);
    }

    [Fact]
    public void MergeUpdate_BetweenDifferentEncryptionTypes_Fires()
    {
        var findings = Scan("""
            MERGE dbo.Customer AS target
            USING (SELECT 1 AS CustomerId) AS src
            ON target.CustomerId = src.CustomerId
            WHEN MATCHED THEN UPDATE SET Notes = Ssn;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(AlwaysEncryptedAssignmentMismatchKind.EncryptionStateMismatch, finding.Kind);
    }
}
