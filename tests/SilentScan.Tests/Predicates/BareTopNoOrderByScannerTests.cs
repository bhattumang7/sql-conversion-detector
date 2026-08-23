using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class BareTopNoOrderByScannerTests
{
    private static IReadOnlyList<BareTopNoOrderByFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return BareTopNoOrderByScanner.Scan(result);
    }

    [Fact]
    public void BareTop_NoOrderBy_Fires()
    {
        var findings = Scan("SELECT TOP (5) * FROM dbo.T;");

        Assert.Single(findings);
    }

    [Fact]
    public void BareTop_PlainInteger_NoParens_Fires()
    {
        var findings = Scan("SELECT TOP 5 * FROM dbo.T;");

        Assert.Single(findings);
    }

    [Fact]
    public void TopWithOrderBy_NeverFires()
    {
        var findings = Scan("SELECT TOP (5) * FROM dbo.T ORDER BY Id;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NoTopAtAll_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.T;");

        Assert.Empty(findings);
    }

    [Fact]
    public void TopHundredPercent_NoOrderBy_NeverFires()
    {

        var findings = Scan("SELECT TOP (100) PERCENT * FROM dbo.T;");

        Assert.Empty(findings);
    }

    [Fact]
    public void TopNinetyNinePercent_NoOrderBy_Fires()
    {

        var findings = Scan("SELECT TOP (99) PERCENT * FROM dbo.T;");

        Assert.Single(findings);
    }

    [Fact]
    public void TopWithTies_AlwaysCarriesOrderBy_NeverFires()
    {

        var findings = Scan("SELECT TOP (5) WITH TIES * FROM dbo.T ORDER BY Id;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NestedSubqueryTop_NoOrderBy_FiresIndependently()
    {
        var findings = Scan(
            "SELECT * FROM (SELECT TOP (3) * FROM dbo.T) AS sub;");

        Assert.Single(findings);
    }

    [Fact]
    public void BareTop_InsideStoredProcedure_Fires()
    {
        var findings = Scan(
            "CREATE PROCEDURE dbo.usp_Find AS BEGIN SELECT TOP (10) Id FROM dbo.T; END");

        Assert.Single(findings);
    }

    [Fact]
    public void BareTop_InsideView_OutermostQuery_Fires()
    {

        var findings = Scan("CREATE VIEW dbo.V AS SELECT TOP (10) Id FROM dbo.T;");

        Assert.Single(findings);
    }

    [Fact]
    public void TwoIndependentBareTops_BothFire()
    {
        var findings = Scan(
            "SELECT TOP (5) * FROM dbo.T; SELECT TOP (3) * FROM dbo.U;");

        Assert.Equal(2, findings.Count);
    }
}
