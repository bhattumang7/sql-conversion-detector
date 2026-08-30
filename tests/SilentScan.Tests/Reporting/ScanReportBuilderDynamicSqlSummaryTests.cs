using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
public sealed class ScanReportBuilderDynamicSqlSummaryTests
{
    [Fact]
    public async Task BuildFromParseResults_MixOfAnalyzedAndUnanalyzableCallSites_PopulatesSummary()
    {

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
        Assert.Contains(report.Find<DynamicSqlFinding>("DynamicSqlScanner"), f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Contains(report.Find<DynamicSqlFinding>("DynamicSqlScanner"), f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
    }

    [Fact]
    public async Task BuildFromParseResults_NoDynamicSqlAtAll_ReportsZeroTotalCallSites()
    {
        var report = await EngineAuthoritativeScan.ScanAsync("CREATE TABLE dbo.T (Col INT NOT NULL);");

        Assert.Equal(0, report.DynamicSqlSummary.TotalCallSites);
    }
}
