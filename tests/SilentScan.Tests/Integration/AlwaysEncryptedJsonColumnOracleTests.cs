using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class AlwaysEncryptedJsonColumnOracleTests : IAsyncLifetime
{
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(AlwaysEncryptedJsonColumnOracleTests)}_{Guid.NewGuid():N}";

    public async Task InitializeAsync() => await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);

    public async Task DisposeAsync() => await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    private static IReadOnlyList<AlwaysEncryptedUnsupportedColumnFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return AlwaysEncryptedUnsupportedColumnScanner.Scan(catalog);
    }

    [Fact]
    public async Task JsonColumn_Encrypted_FailsToDeployWithMsg33280_AndScannerFlagsIt()
    {
        const string KeySetup = """
            CREATE COLUMN MASTER KEY CMK1
            WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/0000000000000000000000000000000000000000');
            """;
        await new ScriptDeployer(Options).DeployAsync(KeySetup, _databaseName);

        const string KeySetup2 = """
            CREATE COLUMN ENCRYPTION KEY CEK1
            WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x016E000001630075007200720065006E00740075007300650072002F006D0079002F0030303030303030303030303030303030303030303030303030303030303030303030303030303030);
            """;
        await new ScriptDeployer(Options).DeployAsync(KeySetup2, _databaseName);

        const string Sql = """
            CREATE TABLE dbo.Customer
            (
                CustomerId INT NOT NULL PRIMARY KEY,
                Payload    JSON
                    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
            );
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(Sql, connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(33280, exception.Number);

        const string KeySetupForScan = """
            CREATE COLUMN MASTER KEY CMK1
            WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/0000000000000000000000000000000000000000');
            GO
            CREATE COLUMN ENCRYPTION KEY CEK1
            WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x01000000);
            GO
            """;
        var finding = Assert.Single(Scan(KeySetupForScan + "\n" + Sql));
        Assert.Equal(AlwaysEncryptedUnsupportedColumnKind.UnsupportedDataType, finding.Kind);
        Assert.Equal("Payload", finding.ColumnName);
    }
}
