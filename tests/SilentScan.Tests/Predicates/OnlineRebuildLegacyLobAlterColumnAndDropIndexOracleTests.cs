using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class OnlineRebuildLegacyLobAlterColumnAndDropIndexOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(OnlineRebuildLegacyLobAlterColumnAndDropIndexOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.LobTable (Id INT NOT NULL, Notes NTEXT NULL);
        CREATE CLUSTERED INDEX CIX_LobTable ON dbo.LobTable (Id);
        CREATE TABLE dbo.PlainTable (Id INT NOT NULL PRIMARY KEY, V VARCHAR(50) NULL);
        CREATE INDEX IX_PlainTable ON dbo.PlainTable (Id);
        """;

    [Fact]
    public async Task AlterColumnOnline_StaysNText_EngineRejectsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "ALTER TABLE dbo.LobTable ALTER COLUMN Notes NTEXT NULL WITH (ONLINE = ON);", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(11427, ex.Number);

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.LobTable (Id INT NOT NULL, Notes NTEXT NULL);
            ALTER TABLE dbo.LobTable ALTER COLUMN Notes NTEXT NULL WITH (ONLINE = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(OnlineRebuildLegacyLobKind.AlterColumnOnline, finding.Kind);
        Assert.Equal("Notes", finding.ColumnName);
    }

    [Fact]
    public async Task AlterColumnOnline_WidensVarchar_EngineAllowsIt_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "ALTER TABLE dbo.PlainTable ALTER COLUMN V VARCHAR(100) NULL WITH (ONLINE = ON);", connection);
        await command.ExecuteNonQueryAsync();

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.PlainTable (Id INT NOT NULL PRIMARY KEY, V VARCHAR(50) NULL);
            ALTER TABLE dbo.PlainTable ALTER COLUMN V VARCHAR(100) NULL WITH (ONLINE = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task AlterColumnOnline_ConvertsIntoNText_EngineRejectsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "ALTER TABLE dbo.PlainTable ALTER COLUMN V NTEXT NULL WITH (ONLINE = ON);", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(11427, ex.Number);

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.PlainTable (Id INT NOT NULL PRIMARY KEY, V VARCHAR(50) NULL);
            ALTER TABLE dbo.PlainTable ALTER COLUMN V NTEXT NULL WITH (ONLINE = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(OnlineRebuildLegacyLobKind.AlterColumnOnline, finding.Kind);
        Assert.Equal("V", finding.ColumnName);
    }

    [Fact]
    public async Task DropIndexOnline_ClusteredOnTableWithNTextColumn_EngineRejectsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DROP INDEX CIX_LobTable ON dbo.LobTable WITH (ONLINE = ON);", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2725, ex.Number);

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.LobTable (Id INT NOT NULL, Notes NTEXT NULL);
            CREATE CLUSTERED INDEX CIX_LobTable ON dbo.LobTable (Id);
            DROP INDEX CIX_LobTable ON dbo.LobTable WITH (ONLINE = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(OnlineRebuildLegacyLobKind.DropIndexOnline, finding.Kind);
        Assert.Equal("Notes", finding.ColumnName);
    }

    [Fact]
    public async Task DropIndexOnline_NonclusteredOnPlainTable_EngineRejectsForUnrelatedReason_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DROP INDEX IX_PlainTable ON dbo.PlainTable WITH (ONLINE = ON);", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(3745, ex.Number);

        var findings = ScanOnlineRebuildLegacyLob("""
            CREATE TABLE dbo.PlainTable (Id INT NOT NULL PRIMARY KEY, V VARCHAR(50) NULL);
            CREATE INDEX IX_PlainTable ON dbo.PlainTable (Id);
            DROP INDEX IX_PlainTable ON dbo.PlainTable WITH (ONLINE = ON);
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
