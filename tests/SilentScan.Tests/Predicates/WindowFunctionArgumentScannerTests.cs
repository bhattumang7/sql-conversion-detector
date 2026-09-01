using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class WindowFunctionArgumentScannerTests
{
    private static IReadOnlyList<WindowFunctionArgumentFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return WindowFunctionArgumentScanner.Scan(result);
    }

    [Fact]
    public void LagWithNegativeLiteralOffset_Fires()
    {
        var findings = Scan("SELECT LAG(Amt, -1) OVER (ORDER BY D) FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.LagLeadNegativeOffset, finding.Kind);
        Assert.Equal("LAG", finding.FunctionName);
    }

    [Fact]
    public void LeadWithNegativeLiteralOffset_Fires()
    {
        var findings = Scan("SELECT LEAD(Amt, -2) OVER (ORDER BY D) FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.LagLeadNegativeOffset, finding.Kind);
        Assert.Equal("LEAD", finding.FunctionName);
    }

    [Fact]
    public void LagWithNegativeFoldableArithmeticOffset_Fires()
    {
        var findings = Scan("SELECT LAG(Amt, 0 - 1) OVER (ORDER BY D) FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.LagLeadNegativeOffset, finding.Kind);
    }

    [Fact]
    public void LagWithPositiveOffset_NeverFires()
    {
        var findings = Scan("SELECT LAG(Amt, 1) OVER (ORDER BY D) FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void LagWithZeroOffset_NeverFires()
    {
        var findings = Scan("SELECT LAG(Amt, 0) OVER (ORDER BY D) FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void LagWithNoOffsetArgument_NeverFires()
    {
        var findings = Scan("SELECT LAG(Amt) OVER (ORDER BY D) FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void LagWithNonFoldableOffset_NeverFires()
    {
        var findings = Scan("SELECT LAG(Amt, @Offset) OVER (ORDER BY D) FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionNamedLagWithNoOverClause_NeverFires()
    {
        var findings = Scan("SELECT dbo.LAG(Amt, -1) FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void PercentileContWithNegativePercentile_Fires()
    {
        var findings = Scan("SELECT PERCENTILE_CONT(-0.1) WITHIN GROUP (ORDER BY Amt) OVER () FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.PercentileOutOfRange, finding.Kind);
        Assert.Equal("PERCENTILE_CONT", finding.FunctionName);
    }

    [Fact]
    public void PercentileDiscWithPercentileAboveOne_Fires()
    {
        var findings = Scan("SELECT PERCENTILE_DISC(1.1) WITHIN GROUP (ORDER BY Amt) OVER () FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.PercentileOutOfRange, finding.Kind);
        Assert.Equal("PERCENTILE_DISC", finding.FunctionName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("0.5")]
    public void PercentileContWithinInclusiveBoundaries_NeverFires(string percentile)
    {
        var findings = Scan($"SELECT PERCENTILE_CONT({percentile}) WITHIN GROUP (ORDER BY Amt) OVER () FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void PercentileContWithNonFoldablePercentile_NeverFires()
    {
        var findings = Scan("SELECT PERCENTILE_CONT(@P) WITHIN GROUP (ORDER BY Amt) OVER () FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void FunctionNamedPercentileContWithNoWithinGroupClause_NeverFires()
    {
        var findings = Scan("SELECT dbo.PERCENTILE_CONT(-0.1) FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void PercentileContAsOrderedSetAggregateWithoutOverClause_StillFires()
    {
        var findings = Scan("SELECT PERCENTILE_CONT(-0.1) WITHIN GROUP (ORDER BY Amt) FROM dbo.Sales;");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.PercentileOutOfRange, finding.Kind);
    }

    [Fact]
    public void TableSampleWithPercentAboveOneHundred_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Sales TABLESAMPLE (150 PERCENT);");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.TableSamplePercentOutOfRange, finding.Kind);
        Assert.Equal("TABLESAMPLE", finding.FunctionName);
    }

    [Fact]
    public void TableSampleWithNegativePercent_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Sales TABLESAMPLE (-1 PERCENT);");

        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.TableSamplePercentOutOfRange, finding.Kind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("50")]
    public void TableSampleWithinInclusiveBoundaries_NeverFires(string percent)
    {
        var findings = Scan($"SELECT * FROM dbo.Sales TABLESAMPLE ({percent} PERCENT);");

        Assert.Empty(findings);
    }

    [Fact]
    public void TableSampleWithRowsOption_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.Sales TABLESAMPLE (1000 ROWS);");

        Assert.Empty(findings);
    }

    [Fact]
    public void TableSampleWithNonFoldablePercent_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.Sales TABLESAMPLE (@P PERCENT);");

        Assert.Empty(findings);
    }

    [Fact]
    public void NoTableSampleClause_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.Sales;");

        Assert.Empty(findings);
    }

    [Fact]
    public void MultipleWindowFunctionsInOneQuery_EachReportedIndependently()
    {
        var findings = Scan(@"
            SELECT
                LAG(Amt, -1) OVER (ORDER BY D) AS Prev,
                PERCENTILE_CONT(1.5) WITHIN GROUP (ORDER BY Amt) OVER () AS Pct
            FROM dbo.Sales;");

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Kind == WindowFunctionArgumentFindingKind.LagLeadNegativeOffset);
        Assert.Contains(findings, f => f.Kind == WindowFunctionArgumentFindingKind.PercentileOutOfRange);
    }
}
