using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class MaxTypedColumnJsonOracleTests : IAsyncLifetime
{
    private static readonly SqlServerOptions Options = new(
        Host: "localhost",
        Port: int.TryParse(Environment.GetEnvironmentVariable("SILENTSCAN_SQL2025_PORT"), out var port) ? port : 14331,
        UserId: "sa",
        Password: Environment.GetEnvironmentVariable("SILENTSCAN_SA_PASSWORD") ?? "SilentScan!Dev2026");

    private readonly string _databaseName = $"{nameof(MaxTypedColumnJsonOracleTests)}_{Guid.NewGuid():N}";

    public async Task InitializeAsync() => await new DatabaseProvisioner(Options).CreateFreshAsync(_databaseName);

    public async Task DisposeAsync() => await new DatabaseProvisioner(Options).DropIfExistsAsync(_databaseName);

    private static IReadOnlyList<MaxTypedColumnFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return MaxTypedColumnScanner.Scan(catalog);
    }

    [Fact]
    public async Task JsonColumn_RejectedAsIndexKeyButAcceptedAsIncludeColumn_SameProfileAsMaxLength_AndScannerFlagsIt()
    {
        const string Ddl = "CREATE TABLE dbo.Documents (Id INT NOT NULL, Body JSON NOT NULL);";
        await new ScriptDeployer(Options).DeployAsync(Ddl, _databaseName);

        await using var connection = new SqlConnection(Options.BuildConnectionString(_databaseName));
        await connection.OpenAsync();

        await using (var keyCommand = new SqlCommand("CREATE INDEX ix_key ON dbo.Documents(Body);", connection))
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() => keyCommand.ExecuteNonQueryAsync());
            Assert.Equal(1978, exception.Number);
        }

        await using (var includeCommand = new SqlCommand("CREATE INDEX ix_include ON dbo.Documents(Id) INCLUDE (Body);", connection))
        {
            await includeCommand.ExecuteNonQueryAsync();
        }

        var finding = Assert.Single(Scan(Ddl));
        Assert.Equal("dbo.Documents", finding.TableQualifiedName);
        Assert.Equal("Body", finding.ColumnName);
        Assert.Equal(NonIndexableColumnFindingKind.MaxLength, finding.Kind);
    }
}
