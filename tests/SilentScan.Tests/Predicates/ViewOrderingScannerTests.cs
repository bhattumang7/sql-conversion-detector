using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds": "TOP(100) PERCENT ignored by the optimizer"
/// and "ORDER BY in a view / inline TVF" - shipped together as <see cref="ViewOrderingFinding"/>.
/// Structural/AST tests; the real ordering-not-propagated mechanism is oracle-confirmed separately
/// (see the finding's own doc comment for the measured Docker-instance behavior).
/// </summary>
public sealed class ViewOrderingScannerTests
{
    private static IReadOnlyList<ViewOrderingFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return ViewOrderingScanner.Scan(result);
    }

    [Fact]
    public void View_TopHundredPercentOrderBy_FiresAsNeverLimits()
    {
        var findings = Scan("CREATE VIEW dbo.v1 AS SELECT TOP (100) PERCENT Id, Amt FROM dbo.T ORDER BY Amt DESC;");

        var finding = Assert.Single(findings);
        Assert.Equal(ViewOrderingFindingKind.TopPercentOrderByNeverLimits, finding.Kind);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Equal("dbo.v1", finding.ObjectQualifiedName);
    }

    [Fact]
    public void View_TopNOrderBy_FiresAsNotGuaranteed()
    {
        var findings = Scan("CREATE VIEW dbo.v1 AS SELECT TOP (10) Id, Amt FROM dbo.T ORDER BY Amt DESC;");

        var finding = Assert.Single(findings);
        Assert.Equal(ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer, finding.Kind);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void View_OffsetFetchOrderBy_FiresAsNotGuaranteed()
    {
        var findings = Scan("CREATE VIEW dbo.v1 AS SELECT Id, Amt FROM dbo.T ORDER BY Amt DESC OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY;");

        var finding = Assert.Single(findings);
        Assert.Equal(ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer, finding.Kind);
    }

    [Fact]
    public void View_NoOrderByAtAll_NeverFires()
    {
        var findings = Scan("CREATE VIEW dbo.v1 AS SELECT Id, Amt FROM dbo.T;");

        Assert.Empty(findings);
    }

    [Fact]
    public void View_TopNonHundredPercent_FiresAsNotGuaranteedNotNeverLimits()
    {
        // TOP (50) PERCENT genuinely excludes rows - only the literal 100 case is provably a no-op.
        var findings = Scan("CREATE VIEW dbo.v1 AS SELECT TOP (50) PERCENT Id, Amt FROM dbo.T ORDER BY Amt DESC;");

        var finding = Assert.Single(findings);
        Assert.Equal(ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer, finding.Kind);
    }

    [Fact]
    public void AlterView_TopHundredPercentOrderBy_Fires()
    {
        var findings = Scan("ALTER VIEW dbo.v1 AS SELECT TOP (100) PERCENT Id FROM dbo.T ORDER BY Id DESC;");

        Assert.Single(findings);
    }

    [Fact]
    public void CreateOrAlterView_TopHundredPercentOrderBy_Fires()
    {
        var findings = Scan("CREATE OR ALTER VIEW dbo.v1 AS SELECT TOP (100) PERCENT Id FROM dbo.T ORDER BY Id DESC;");

        Assert.Single(findings);
    }

    [Fact]
    public void InlineTvf_TopHundredPercentOrderBy_Fires()
    {
        var findings = Scan("CREATE FUNCTION dbo.fn1() RETURNS TABLE AS RETURN (SELECT TOP (100) PERCENT Id FROM dbo.T ORDER BY Id DESC);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.fn1", finding.ObjectQualifiedName);
    }

    [Fact]
    public void MultiStatementTvf_NeverFires()
    {
        // No single outermost query to inspect the same way - INSERT...SELECT ordering never
        // survives into the returned table shape anyway, so this is never a candidate.
        var findings = Scan(@"
            CREATE FUNCTION dbo.fn1() RETURNS @t TABLE (Id INT)
            AS
            BEGIN
                INSERT INTO @t (Id) SELECT TOP (100) PERCENT Id FROM dbo.T ORDER BY Id DESC;
                RETURN;
            END");

        Assert.Empty(findings);
    }

    [Fact]
    public void ScalarFunction_NeverFires()
    {
        var findings = Scan(@"
            CREATE FUNCTION dbo.fn1(@x INT) RETURNS INT
            AS
            BEGIN
                RETURN @x;
            END");

        Assert.Empty(findings);
    }

    [Fact]
    public void PlainSelectNotInViewOrFunction_NeverFires()
    {
        // The checklist's own stated scope is "view / inline TVF" only - a plain top-level SELECT
        // (or a derived table/CTE using the identical trick) is a known, deliberate v1 scope limit.
        var findings = Scan("SELECT TOP (100) PERCENT Id FROM dbo.T ORDER BY Id DESC;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ViewWithUnionTopLevel_DeclinesRatherThanGuessing()
    {
        var findings = Scan("CREATE VIEW dbo.v1 AS (SELECT TOP (100) PERCENT Id FROM dbo.T ORDER BY Id) UNION ALL SELECT Id FROM dbo.U;");

        Assert.Empty(findings);
    }
}
