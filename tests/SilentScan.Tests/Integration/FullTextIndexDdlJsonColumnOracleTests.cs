using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class FullTextIndexDdlJsonColumnOracleTests : IAsyncLifetime
{
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(FullTextIndexDdlJsonColumnOracleTests)}_{Guid.NewGuid():N}";

    public async Task InitializeAsync() => await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);

    public async Task DisposeAsync() => await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    private static IReadOnlyList<FullTextIndexDdlFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return FullTextIndexDdlScanner.Scan(catalog);
    }

    [Fact]
    public async Task NativeJsonColumn_FailsToDeployWithMsg7670_AndScannerFlagsIt()
    {
        const string Ddl = """
            CREATE FULLTEXT CATALOG DdlCatalog AS DEFAULT;
            CREATE TABLE dbo.TypeCheck (Id INT NOT NULL CONSTRAINT PK_TypeCheck PRIMARY KEY, Doc JSON NULL);
            """;
        await new ScriptDeployer(Options).DeployAsync(Ddl, _databaseName);

        const string Sql = "CREATE FULLTEXT INDEX ON dbo.TypeCheck(Doc) KEY INDEX PK_TypeCheck;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(Sql, connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(7670, exception.Number);

        var findings = Scan(Ddl + "\n" + Sql);
        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.UnsupportedColumnType, finding.Kind);
        Assert.Equal("Doc", finding.ColumnName);
    }
}
