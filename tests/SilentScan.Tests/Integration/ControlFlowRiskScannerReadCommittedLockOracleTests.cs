using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ControlFlowRiskScannerReadCommittedLockOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ControlFlowRiskScannerReadCommittedLockOracleTests);

    protected override string Ddl => """
        ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON;
        GO
        CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY);
        """;

    private const string ReadCommittedLockSql = """
        SELECT Id FROM dbo.Widgets WITH (READCOMMITTEDLOCK) WHERE Id = 1;
        """;

    private const string NoLockSql = """
        SELECT Id FROM dbo.Widgets WITH (NOLOCK) WHERE Id = 1;
        """;

    [Fact]
    public async Task ReadAsync_ReadCommittedSnapshotOn_MatchesRealServerConfiguration()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();";
        var real = (bool)(await command.ExecuteScalarAsync())!;

        Assert.True(real);
        Assert.Equal(real, catalog.IsReadCommittedSnapshotOn);
    }

    [Fact]
    public async Task Scan_ReadCommittedLockHintWithRcsiOn_ReportsRevertsToBlockingFinding()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        Assert.True(catalog.IsReadCommittedSnapshotOn);

        var result = SqlScriptParser.ParseText("read-committed-lock.sql", ReadCommittedLockSql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var findings = ControlFlowRiskScanner.Scan(result, catalog);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.ReadCommittedLockRevertsRowVersioning);
        Assert.Contains("READ_COMMITTED_SNAPSHOT", finding.DetailText);
        Assert.Contains("READCOMMITTEDLOCK", finding.DetailText);
        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.DirtyReadIsolationHint);
    }

    [Fact]
    public async Task Scan_NoLockHintWithRcsiOn_StillOnlyReportsDirtyReadNotRcsiReversion()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        Assert.True(catalog.IsReadCommittedSnapshotOn);

        var result = SqlScriptParser.ParseText("nolock.sql", NoLockSql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var findings = ControlFlowRiskScanner.Scan(result, catalog);

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.DirtyReadIsolationHint);
        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.ReadCommittedLockRevertsRowVersioning);
    }
}
