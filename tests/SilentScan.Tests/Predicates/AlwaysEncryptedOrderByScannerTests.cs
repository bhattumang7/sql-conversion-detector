using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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
        GO
        CREATE TABLE dbo.Orders (CustomerId INT NOT NULL, Amount INT NOT NULL);
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
    public void OrderByOrdinalPositionOfEncryptedColumn_Fires()
    {
        var findings = Scan("SELECT CustomerId, Ssn FROM dbo.Customer ORDER BY 2;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customer", finding.TableQualifiedName);
        Assert.Equal("Ssn", finding.ColumnName);
        Assert.Equal("Deterministic", finding.EncryptionTypeDisplay);
    }

    [Fact]
    public void OrderByOrdinalPositionOfPlainColumn_NeverFires()
    {
        var findings = Scan("SELECT CustomerId, PlainName FROM dbo.Customer ORDER BY 2;");

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

    [Fact]
    public void OrderByOuterAliasEncryptedColumn_InsideCorrelatedApplySubquery_Fires()
    {
        var findings = Scan(
            "SELECT c.CustomerId FROM dbo.Customer c "
            + "CROSS APPLY (SELECT TOP 1 o.Amount FROM dbo.Orders o WHERE o.CustomerId = c.CustomerId ORDER BY c.Ssn) t;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Customer", finding.TableQualifiedName);
        Assert.Equal("Ssn", finding.ColumnName);
    }
}
