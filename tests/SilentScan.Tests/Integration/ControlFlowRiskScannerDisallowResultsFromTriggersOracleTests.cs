using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
[Collection("ServerLevelConfiguration")]
public sealed class ControlFlowRiskScannerDisallowResultsFromTriggersOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ControlFlowRiskScannerDisallowResultsFromTriggersOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY);
        """;

    private const string SelectTriggerSql = """
        CREATE TRIGGER dbo.trg_Widgets_Select ON dbo.Widgets AFTER INSERT AS
        BEGIN
            SELECT * FROM inserted;
        END;
        """;

    private const string PrintTriggerSql = """
        CREATE TRIGGER dbo.trg_Widgets_Print ON dbo.Widgets AFTER INSERT AS
        BEGIN
            PRINT 'fired';
        END;
        """;

    [Fact]
    public async Task ReadAsync_DisallowResultsFromTriggers_MatchesRealServerConfiguration()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'disallow results from triggers';";
        var real = (int)(await command.ExecuteScalarAsync())! != 0;

        Assert.Equal(real, catalog.IsDisallowResultsFromTriggersEnabled);
    }

    [Fact]
    public async Task Scan_DisallowResultsFromTriggersEnabled_ReportsHardFailInsteadOfForwarding()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        var (originalShowAdvanced, originalDisallowResults) = await ReadServerTriggerSettingsAsync();

        try
        {
            await SetDisallowResultsFromTriggersAsync(disallowResults: 1, restoreShowAdvancedTo: 1);

            var catalog = await new LiveCatalogReader(connectionString).ReadAsync();
            Assert.True(catalog.IsDisallowResultsFromTriggersEnabled);

            var selectResult = SqlScriptParser.ParseText("select-trigger.sql", SelectTriggerSql);
            Assert.False(selectResult.HasErrors, string.Join("; ", selectResult.Errors.Select(e => e.Message)));
            var selectFindings = ControlFlowRiskScanner.Scan(selectResult, catalog);
            var selectFinding = Assert.Single(selectFindings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
            Assert.Contains("Msg 524", selectFinding.DetailText);
            Assert.Contains("rolls back the triggering DML", selectFinding.DetailText);
            Assert.DoesNotContain("sends output back", selectFinding.DetailText);

            var printResult = SqlScriptParser.ParseText("print-trigger.sql", PrintTriggerSql);
            Assert.False(printResult.HasErrors, string.Join("; ", printResult.Errors.Select(e => e.Message)));
            var printFindings = ControlFlowRiskScanner.Scan(printResult, catalog);
            var printFinding = Assert.Single(printFindings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
            Assert.Contains("sends a message back", printFinding.DetailText);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var createTrigger = connection.CreateCommand())
            {
                createTrigger.CommandText = SelectTriggerSql;
                await createTrigger.ExecuteNonQueryAsync();
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO dbo.Widgets (Id) VALUES (1);";
            var sqlEx = await Assert.ThrowsAsync<SqlException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(524, sqlEx.Number);

            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM dbo.Widgets;";
            Assert.Equal(0, (int)(await countCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            await SetDisallowResultsFromTriggersAsync(originalDisallowResults, originalShowAdvanced);
        }
    }

    [Fact]
    public async Task Scan_DisallowResultsFromTriggersDisabled_ReportsForwardingClaim()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        Assert.NotEqual(true, catalog.IsDisallowResultsFromTriggersEnabled);

        var selectResult = SqlScriptParser.ParseText("select-trigger.sql", SelectTriggerSql);
        Assert.False(selectResult.HasErrors, string.Join("; ", selectResult.Errors.Select(e => e.Message)));
        var selectFindings = ControlFlowRiskScanner.Scan(selectResult, catalog);
        var selectFinding = Assert.Single(selectFindings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
        Assert.Contains("sends output back", selectFinding.DetailText);
        Assert.DoesNotContain("Msg 524", selectFinding.DetailText);
    }

    private async Task<(int ShowAdvanced, int DisallowResults)> ReadServerTriggerSettingsAsync()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'show advanced options'),
                (SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'disallow results from triggers');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task SetDisallowResultsFromTriggersAsync(int disallowResults, int restoreShowAdvancedTo)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            EXEC sp_configure 'show advanced options', 1;
            RECONFIGURE;
            EXEC sp_configure 'disallow results from triggers', {disallowResults};
            RECONFIGURE;
            EXEC sp_configure 'show advanced options', {restoreShowAdvancedTo};
            RECONFIGURE;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
