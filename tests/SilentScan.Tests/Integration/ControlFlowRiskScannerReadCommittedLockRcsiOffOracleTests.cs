using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ControlFlowRiskScannerReadCommittedLockRcsiOffOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ControlFlowRiskScannerReadCommittedLockRcsiOffOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY);
        """;

    private const string ReadCommittedLockSql = """
        SELECT Id FROM dbo.Widgets WITH (READCOMMITTEDLOCK) WHERE Id = 1;
        """;

    [Fact]
    public async Task Scan_ReadCommittedLockHintWithRcsiOff_NeverFires()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        Assert.NotEqual(true, catalog.IsReadCommittedSnapshotOn);

        var result = SqlScriptParser.ParseText("read-committed-lock.sql", ReadCommittedLockSql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var findings = ControlFlowRiskScanner.Scan(result, catalog);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.ReadCommittedLockRevertsRowVersioning);
    }
}
