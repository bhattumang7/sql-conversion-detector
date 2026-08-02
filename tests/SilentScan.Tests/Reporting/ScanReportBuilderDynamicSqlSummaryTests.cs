using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;

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
    public void BuildFromParseResults_MixOfAnalyzedAndUnanalyzableCallSites_PopulatesSummary()
    {
        var result = SqlScriptParser.ParseText(
            "proc.sql",
            """
            CREATE TABLE dbo.T (Col INT NOT NULL);
            GO
            EXEC('SELECT Col FROM dbo.T WHERE Col = 1');
            GO
            EXEC(@UndeclaredSql);
            """);

        var report = ScanReportBuilder.BuildFromParseResults([result]);

        Assert.Equal(2, report.DynamicSqlSummary.TotalCallSites);
        Assert.Equal(1, report.DynamicSqlSummary.AnalyzedCount);
        Assert.Equal(1, report.DynamicSqlSummary.UnanalyzableCount);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
    }

    [Fact]
    public void BuildFromParseResults_NoDynamicSqlAtAll_ReportsZeroTotalCallSites()
    {
        var result = SqlScriptParser.ParseText("t.sql", "CREATE TABLE dbo.T (Col INT NOT NULL);");

        var report = ScanReportBuilder.BuildFromParseResults([result]);

        Assert.Equal(0, report.DynamicSqlSummary.TotalCallSites);
    }
}
