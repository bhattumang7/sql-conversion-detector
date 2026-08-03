using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// Before this, DynamicSqlSummary was only ever computed by VerifyCorpusCommand - a plain
/// `scan`/`scan-corpus` run carried the raw DynamicSqlFindings list with no rollup anywhere in
/// its own output, so the "X% of dynamic SQL call sites we could not analyze" figure CLAUDE.md's
/// dynamic SQL policy requires had to be hand-counted from the finding list (an audit finding).
/// </summary>
public sealed class ScanReportBuilderDynamicSqlSummaryTests
{
    [Fact]
    public async Task BuildFromParseResults_MixOfAnalyzedAndUnanalyzableCallSites_PopulatesSummary()
    {
        // Both EXEC statements have to live inside a real module body - a bare top-level EXEC
        // isn't captured by sys.sql_modules at all, so under the engine-authoritative pipeline
        // (module text read back from the server, not parsed straight off disk) a call site
        // outside any CREATE PROCEDURE/FUNCTION/TRIGGER would never reach the dynamic SQL
        // scanner in the first place.
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.T (Col INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_Dynamic
            AS
            BEGIN
                EXEC('SELECT Col FROM dbo.T WHERE Col = 1');

                DECLARE @UndeclaredSql NVARCHAR(MAX);
                SELECT @UndeclaredSql = N'SELECT 1 WHERE 1 = 0' FROM (SELECT 1 AS x) AS Dummy;
                EXEC(@UndeclaredSql);
            END
            """);

        Assert.Equal(2, report.DynamicSqlSummary.TotalCallSites);
        Assert.Equal(1, report.DynamicSqlSummary.AnalyzedCount);
        Assert.Equal(1, report.DynamicSqlSummary.UnanalyzableCount);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
    }

    [Fact]
    public async Task BuildFromParseResults_NoDynamicSqlAtAll_ReportsZeroTotalCallSites()
    {
        var report = await EngineAuthoritativeScan.ScanAsync("CREATE TABLE dbo.T (Col INT NOT NULL);");

        Assert.Equal(0, report.DynamicSqlSummary.TotalCallSites);
    }
}
