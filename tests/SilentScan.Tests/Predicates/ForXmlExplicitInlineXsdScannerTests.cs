using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ForXmlExplicitInlineXsdScannerTests
{
    private static IReadOnlyList<ForXmlExplicitInlineXsdFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        return ForXmlExplicitInlineXsdScanner.Scan(result);
    }

    [Fact]
    public void ExplicitWithXmlSchema_Fires()
    {
        var findings = Scan("""
            SELECT 1 AS Tag, NULL AS Parent, name AS [Row!1!name]
            FROM sys.objects
            FOR XML EXPLICIT, XMLSCHEMA;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void ExplicitWithoutXmlSchema_NeverFires()
    {
        var findings = Scan("""
            SELECT 1 AS Tag, NULL AS Parent, name AS [Row!1!name]
            FROM sys.objects
            FOR XML EXPLICIT;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void AutoWithXmlSchema_NeverFires()
    {
        var findings = Scan("""
            SELECT name
            FROM sys.objects
            FOR XML AUTO, XMLSCHEMA;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NoForXmlClause_NeverFires()
    {
        var findings = Scan("SELECT name FROM sys.objects;");

        Assert.Empty(findings);
    }
}
