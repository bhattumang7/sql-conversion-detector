using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "Bare TOP (n) with no
/// ORDER BY anywhere in the query" - see <see cref="BareTopNoOrderByFinding"/> for the full scope/
/// precision story, including the documented-vs-reproduced determinism claim. Pure AST, no catalog
/// needed at all.
/// </summary>
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
        // 100 percent of a result set is every row regardless of TOP's own row-selection
        // nondeterminism - deliberately excluded, see this finding's own doc comment.
        var findings = Scan("SELECT TOP (100) PERCENT * FROM dbo.T;");

        Assert.Empty(findings);
    }

    [Fact]
    public void TopNinetyNinePercent_NoOrderBy_Fires()
    {
        // Every percent value other than exactly 100 genuinely narrows the row set to an
        // arbitrary, unrepeatable subset.
        var findings = Scan("SELECT TOP (99) PERCENT * FROM dbo.T;");

        Assert.Single(findings);
    }

    [Fact]
    public void TopWithTies_AlwaysCarriesOrderBy_NeverFires()
    {
        // TOP ... WITH TIES requires ORDER BY at the grammar level (Msg 1082 otherwise) - this
        // shape is structurally unreachable with a null OrderByClause, confirmed here rather than
        // just asserted.
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
        // Unlike ViewOrderingFinding, this scanner is not limited to a view's outermost query -
        // but it should still fire there when the shape appears.
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
