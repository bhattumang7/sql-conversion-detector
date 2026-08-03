using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the disabled-index precision bug (formerly pinned in
/// KnownGapCharacterizationTests.DisabledIndex_IsStillReportedAsIndexed): a column behind an
/// index that has been <c>ALTER INDEX ... DISABLE</c>d must never report Indexed = true, since
/// the engine cannot actually use that index to seek. Runs through
/// <see cref="ScanReportBuilder"/>, the same entry point production uses, so it exercises the
/// full catalog -> lineage -> predicate pipeline, not CatalogBuilder in isolation - and the
/// ScanForced verdict is confirmed against the real oracle (CLAUDE.md: verify the real thing).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DisabledIndexPipelineTests : OracleTestFixture
{
    private const string DisabledIndexSql = """
        CREATE TABLE dbo.Devices (SerialNo varchar(50) NOT NULL);
        GO
        CREATE INDEX IX_SerialNo ON dbo.Devices (SerialNo);
        GO
        ALTER INDEX IX_SerialNo ON dbo.Devices DISABLE;
        GO
        SELECT 1 FROM dbo.Devices WHERE SerialNo = N'ABC';
        """;

    protected override string DatabaseNameSeed => nameof(DisabledIndexPipelineTests);

    protected override string Ddl => DisabledIndexSql;

    [Fact]
    public async Task DisabledIndex_NoLongerReportsIndexed_OracleConfirmed()
    {
        var parseResult = SqlScriptParser.ParseText("disabled_index.sql", DisabledIndexSql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "SerialNo");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.False(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task RebuiltIndex_ReportsIndexedAgain_OracleConfirmed()
    {
        const string rebuiltIndexSql = """
            CREATE TABLE dbo.Devices2 (SerialNo varchar(50) NOT NULL);
            GO
            CREATE INDEX IX_SerialNo ON dbo.Devices2 (SerialNo);
            GO
            ALTER INDEX IX_SerialNo ON dbo.Devices2 DISABLE;
            GO
            ALTER INDEX IX_SerialNo ON dbo.Devices2 REBUILD;
            GO
            SELECT 1 FROM dbo.Devices2 WHERE SerialNo = N'ABC';
            """;
        await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployWhitelistedDdlAsync(rebuiltIndexSql, DatabaseName);

        var parseResult = SqlScriptParser.ParseText("rebuilt_index.sql", rebuiltIndexSql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "SerialNo");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }
}
