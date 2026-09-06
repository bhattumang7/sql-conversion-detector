using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class RevertCookieTypeMismatchScannerTests
{
    private static IReadOnlyList<RevertCookieTypeMismatchFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return RevertCookieTypeMismatchScanner.Scan(result, catalog);
    }

    [Fact]
    public void NarrowerVarbinaryCookie_Fires()
    {
        var findings = Scan("""
            DECLARE @cookie varbinary(10);
            REVERT WITH COOKIE = @cookie;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@cookie", finding.CookieVariableName);
    }

    [Fact]
    public void WiderVarbinaryCookie_NeverFires()
    {
        var findings = Scan("""
            DECLARE @cookie varbinary(200);
            REVERT WITH COOKIE = @cookie;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void JustBelowMinimumVarbinaryCookie_Fires()
    {
        var findings = Scan("""
            DECLARE @cookie varbinary(49);
            REVERT WITH COOKIE = @cookie;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void MinimumVarbinaryCookie_NeverFires()
    {
        var findings = Scan("""
            DECLARE @cookie varbinary(50);
            REVERT WITH COOKIE = @cookie;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MaxVarbinaryCookie_Fires()
    {
        var findings = Scan("""
            DECLARE @cookie varbinary(max);
            REVERT WITH COOKIE = @cookie;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void NonBinaryCookie_Fires()
    {
        var findings = Scan("""
            DECLARE @cookie int;
            REVERT WITH COOKIE = @cookie;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void ExactVarbinary100Cookie_NeverFires()
    {
        var findings = Scan("""
            DECLARE @cookie varbinary(100);
            REVERT WITH COOKIE = @cookie;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ProcedureParameterTypedVarbinary100_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.TestProc (@cookie varbinary(100))
            AS
            BEGIN
                REVERT WITH COOKIE = @cookie;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ProcedureParameterTypedVarbinary10_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.TestProc (@cookie varbinary(10))
            AS
            BEGIN
                REVERT WITH COOKIE = @cookie;
            END
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void RevertWithoutCookie_NeverFires()
    {
        var findings = Scan("REVERT;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnresolvedCookieVariable_NeverFires()
    {
        var findings = Scan("REVERT WITH COOKIE = @undeclared;");

        Assert.Empty(findings);
    }
}
