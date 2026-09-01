using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class OnlineRebuildLegacyLobOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(OnlineRebuildLegacyLobOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.LobTable (Id INT NOT NULL PRIMARY KEY, Notes NTEXT NULL);
        CREATE TABLE dbo.PlainTable (Id INT NOT NULL PRIMARY KEY, Notes NVARCHAR(MAX) NULL);
        CREATE INDEX IX_LobTable ON dbo.LobTable (Id);
        """;

    [Fact]
    public async Task AlterTableRebuild_Online_NTextColumn_EngineRejectsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand("ALTER TABLE dbo.LobTable REBUILD WITH (ONLINE = ON);", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2725, ex.Number);

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.LobTable (Id INT NOT NULL PRIMARY KEY, Notes NTEXT NULL);
            ALTER TABLE dbo.LobTable REBUILD WITH (ONLINE = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(OnlineRebuildLegacyLobKind.AlterTableRebuild, finding.Kind);
    }

    [Fact]
    public async Task AlterTableRebuild_Online_NoLegacyLobColumn_EngineAllowsIt_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand("ALTER TABLE dbo.PlainTable REBUILD WITH (ONLINE = ON);", connection);
        await command.ExecuteNonQueryAsync();

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.PlainTable (Id INT NOT NULL PRIMARY KEY, Notes NVARCHAR(MAX) NULL);
            ALTER TABLE dbo.PlainTable REBUILD WITH (ONLINE = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task AlterIndexAllRebuild_Online_NTextColumn_EngineRejectsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand("ALTER INDEX ALL ON dbo.LobTable REBUILD WITH (ONLINE = ON);", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2725, ex.Number);

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.LobTable (Id INT NOT NULL PRIMARY KEY, Notes NTEXT NULL);
            ALTER INDEX ALL ON dbo.LobTable REBUILD WITH (ONLINE = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(OnlineRebuildLegacyLobKind.AlterIndexAllRebuild, finding.Kind);
    }

    [Fact]
    public async Task AlterIndexSingleNamedRebuild_Online_IndexWithoutLobColumn_EngineAllowsIt_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand("ALTER INDEX IX_LobTable ON dbo.LobTable REBUILD WITH (ONLINE = ON);", connection);
        await command.ExecuteNonQueryAsync();

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.LobTable (Id INT NOT NULL PRIMARY KEY, Notes NTEXT NULL);
            CREATE INDEX IX_LobTable ON dbo.LobTable (Id);
            ALTER INDEX IX_LobTable ON dbo.LobTable REBUILD WITH (ONLINE = ON);
            """);

        Assert.Empty(findings);
    }

    private static IReadOnlyList<OnlineRebuildLegacyLobFinding> ScanOnlineRebuildLegacyLob(string sql)
    {
        var parsed = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parsed]);
        return OnlineRebuildLegacyLobScanner.Scan(catalog);
    }
}
