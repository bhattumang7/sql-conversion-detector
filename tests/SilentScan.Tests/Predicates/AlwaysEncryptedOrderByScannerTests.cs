using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// See <see cref="AlwaysEncryptedOrderByFinding"/> for the full scope/precision story, including
/// this scanner's own known v1 scope limit (top-level SELECT ORDER BY only, no window-function
/// OVER/view/CTE resolution).
/// </summary>
public sealed class AlwaysEncryptedOrderByScannerTests
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
            PlainName  NVARCHAR(100) NOT NULL
        );
        """;

    private static IReadOnlyList<AlwaysEncryptedOrderByFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{BaseDdl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return AlwaysEncryptedOrderByScanner.Scan(result, catalog);
    }

    [Fact]
    public void OrderByDeterministicColumn_Fires()
    {
        var findings = Scan("SELECT CustomerId FROM dbo.Customer ORDER BY Ssn;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customer", finding.TableQualifiedName);
        Assert.Equal("Ssn", finding.ColumnName);
        Assert.Equal("Deterministic", finding.EncryptionTypeDisplay);
    }

    [Fact]
    public void OrderByRandomizedColumn_Fires()
    {
        var findings = Scan("SELECT CustomerId FROM dbo.Customer ORDER BY Notes;");

        var finding = Assert.Single(findings);
        Assert.Equal("Notes", finding.ColumnName);
        Assert.Equal("Randomized", finding.EncryptionTypeDisplay);
    }

    [Fact]
    public void OrderByEncryptedColumn_QualifiedByAlias_Fires()
    {
        var findings = Scan("SELECT c.CustomerId FROM dbo.Customer c ORDER BY c.Ssn;");

        Assert.Single(findings);
    }

    [Fact]
    public void OrderByEncryptedColumn_Descending_Fires()
    {
        var findings = Scan("SELECT CustomerId FROM dbo.Customer ORDER BY Ssn DESC;");

        Assert.Single(findings);
    }

    [Fact]
    public void OrderByPlainColumn_NeverFires()
    {
        var findings = Scan("SELECT CustomerId FROM dbo.Customer ORDER BY PlainName;");

        Assert.Empty(findings);
    }

    [Fact]
    public void OrderByPrimaryKeyColumn_NeverFires()
    {
        var findings = Scan("SELECT CustomerId FROM dbo.Customer ORDER BY CustomerId;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NoOrderByClause_NeverFires()
    {
        var findings = Scan("SELECT CustomerId FROM dbo.Customer WHERE Ssn = 'x';");

        Assert.Empty(findings);
    }

    [Fact]
    public void MultipleEncryptedColumnsInOrderBy_FiresOncePerColumn()
    {
        var findings = Scan("SELECT CustomerId FROM dbo.Customer ORDER BY Ssn, Notes;");

        Assert.Equal(2, findings.Count);
        Assert.Equal(["Ssn", "Notes"], findings.Select(f => f.ColumnName));
    }
}
