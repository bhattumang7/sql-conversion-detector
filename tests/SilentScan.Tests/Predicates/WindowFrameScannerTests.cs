using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class WindowFrameScannerTests
{
    private static IReadOnlyList<WindowFrameFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return WindowFrameScanner.Scan(result);
    }

    [Fact]
    public void ExplicitRowsFrame_NeverFires()
    {
        var findings = Scan("SELECT SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ExplicitRangeFrame_Fires()
    {
        var findings = Scan("SELECT SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFrameFindingKind.ExplicitRangeFrame, finding.Kind);
    }

    [Fact]
    public void OrderByWithNoFrameClause_FiresAsImplicitDefaultRange()
    {
        var findings = Scan("SELECT SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D) FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFrameFindingKind.ImplicitDefaultRangeFrame, finding.Kind);
    }

    [Fact]
    public void NoOrderByAtAll_NeverFires()
    {
        var findings = Scan("SELECT SUM(Amt) OVER (PARTITION BY GroupId) FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void RowNumberWithOrderByNoFrame_FiresAsImplicitDefaultRange()
    {
        var findings = Scan("SELECT ROW_NUMBER() OVER (PARTITION BY GroupId ORDER BY D) FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFrameFindingKind.ImplicitDefaultRangeFrame, finding.Kind);
    }

    [Fact]
    public void MultipleWindowFunctionsInOneQuery_EachReportedIndependently()
    {
        var findings = Scan(@"
            SELECT
                SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningRows,
                SUM(Amt) OVER (PARTITION BY GroupId ORDER BY D RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningRange
            FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFrameFindingKind.ExplicitRangeFrame, finding.Kind);
    }
}
