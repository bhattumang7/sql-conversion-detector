using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class AlwaysEncryptedAssignmentMismatchOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(AlwaysEncryptedAssignmentMismatchOracleTests);

    protected override string Ddl => """
        CREATE COLUMN MASTER KEY AeamCmk
        WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/5555555555555555555555555555555555555555');
        GO
        CREATE COLUMN ENCRYPTION KEY AeamCek
        WITH VALUES (COLUMN_MASTER_KEY = AeamCmk, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
        GO
        CREATE TABLE dbo.Customer
        (
            CustomerId INT NOT NULL PRIMARY KEY,
            Ssn        NVARCHAR(20) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = AeamCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            Notes      NVARCHAR(200) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = AeamCek, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            PlainName  NVARCHAR(100) NOT NULL
        );
        """;

    private const string ScannerDdl = """
        CREATE TABLE dbo.Customer
        (
            CustomerId INT NOT NULL PRIMARY KEY,
            Ssn        NVARCHAR(20) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = AeamCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            Notes      NVARCHAR(200) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = AeamCek, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL,
            PlainName  NVARCHAR(100) NOT NULL
        );
        """;

    private static IReadOnlyList<AlwaysEncryptedAssignmentMismatchFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{ScannerDdl}\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return AlwaysEncryptedAssignmentMismatchScanner.Scan(result, catalog);
    }

    private async Task<SqlException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
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
    public async Task UpdateSet_LiteralIntoEncryptedColumn_FailsWithMsg206_AndScannerFlagsIt()
    {
        var exception = await ExecuteExpectingFailureAsync("UPDATE dbo.Customer SET Ssn = N'123' WHERE CustomerId = 1;");

        Assert.Equal(206, exception.Number);

        var finding = Assert.Single(Scan("UPDATE dbo.Customer SET Ssn = N'123' WHERE CustomerId = 1;"));
        Assert.Equal(AlwaysEncryptedAssignmentMismatchKind.LiteralSource, finding.Kind);
        Assert.Equal("Ssn", finding.TargetColumnName);
    }

    [Fact]
    public async Task UpdateSet_BetweenDifferentEncryptionTypes_FailsWithMsg206_AndScannerFlagsIt()
    {
        var exception = await ExecuteExpectingFailureAsync("UPDATE dbo.Customer SET Notes = Ssn WHERE CustomerId = 1;");

        Assert.Equal(206, exception.Number);

        var finding = Assert.Single(Scan("UPDATE dbo.Customer SET Notes = Ssn WHERE CustomerId = 1;"));
        Assert.Equal(AlwaysEncryptedAssignmentMismatchKind.EncryptionStateMismatch, finding.Kind);
        Assert.Equal("Notes", finding.TargetColumnName);
        Assert.Equal("Ssn", finding.SourceColumnName);
    }

    [Fact]
    public async Task UpdateSet_EncryptedIntoPlainColumn_FailsWithMsg206_AndScannerFlagsIt()
    {
        var exception = await ExecuteExpectingFailureAsync("UPDATE dbo.Customer SET PlainName = Ssn WHERE CustomerId = 1;");

        Assert.Equal(206, exception.Number);

        var finding = Assert.Single(Scan("UPDATE dbo.Customer SET PlainName = Ssn WHERE CustomerId = 1;"));
        Assert.Equal(AlwaysEncryptedAssignmentMismatchKind.EncryptionStateMismatch, finding.Kind);
    }

    [Fact]
    public async Task UpdateSet_NullIntoEncryptedColumn_Succeeds_AndScannerDoesNotFlagIt()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("UPDATE dbo.Customer SET Ssn = NULL WHERE CustomerId = 1;"));

        Assert.Null(exception);
        Assert.Empty(Scan("UPDATE dbo.Customer SET Ssn = NULL WHERE CustomerId = 1;"));
    }

    [Fact]
    public async Task UpdateSet_BetweenSameEncryptedColumn_Succeeds_AndScannerDoesNotFlagIt()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("UPDATE dbo.Customer SET Ssn = Ssn WHERE CustomerId = 1;"));

        Assert.Null(exception);
        Assert.Empty(Scan("UPDATE dbo.Customer SET Ssn = Ssn WHERE CustomerId = 1;"));
    }
}
