using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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
        var report = await EngineAuthoritativeScan.ScanAsync(DisabledIndexSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "SerialNo");
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

        var report = await EngineAuthoritativeScan.ScanAsync(rebuiltIndexSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "SerialNo");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }
}
