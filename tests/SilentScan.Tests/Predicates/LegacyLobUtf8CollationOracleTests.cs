using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class LegacyLobUtf8CollationOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LegacyLobUtf8CollationOracleTests);

    protected override string Ddl => string.Empty;

    private static IReadOnlyList<LegacyLobUtf8CollationFinding> Scan(string sql)
    {
        var parsed = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parsed]);
        return LegacyLobUtf8CollationScanner.Scan(catalog);
    }

    [Fact]
    public async Task TextColumn_Utf8Collation_EngineRejectsIt_ScannerMustFlag()
    {
        const string ddl = "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, C1 TEXT COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL);";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(ddl, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(4188, exception.Number);

        var finding = Assert.Single(Scan(ddl));
        Assert.Equal("C1", finding.ColumnName);
    }

    [Fact]
    public async Task NTextColumn_SupplementaryCharacterAwareCollation_EngineRejectsIt_ScannerMustFlag()
    {
        const string ddl = "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, C1 NTEXT COLLATE Latin1_General_100_CI_AS_SC NULL);";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(ddl, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(4188, exception.Number);

        var finding = Assert.Single(Scan(ddl));
        Assert.Equal("C1", finding.ColumnName);
    }

    [Fact]
    public async Task NTextColumn_OrdinaryCollation_EngineAcceptsIt_ScannerMustNotFlag()
    {
        const string ddl = "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, C1 NTEXT COLLATE Latin1_General_100_CI_AS NULL);";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(ddl, connection);
        await command.ExecuteNonQueryAsync();

        Assert.Empty(Scan(ddl));
    }
}
