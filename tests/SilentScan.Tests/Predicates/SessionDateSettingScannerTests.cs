using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class SessionDateSettingScannerTests
{
    private static IReadOnlyList<SessionDateSettingFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return SessionDateSettingScanner.Scan(result);
    }

    [Fact]
    public void SetDateFormat_Fires()
    {
        var findings = Scan("SET DATEFORMAT mdy;");

        var finding = Assert.Single(findings);
        Assert.Equal(SessionDateSettingKind.DateFormat, finding.Kind);
    }

    [Fact]
    public void SetDateFirst_Fires()
    {
        var findings = Scan("SET DATEFIRST 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(SessionDateSettingKind.DateFirst, finding.Kind);
    }

    [Fact]
    public void BothInSameModule_BothFire()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                SET DATEFORMAT ymd;
                SET DATEFIRST 7;
                SELECT 1;
            END
            """);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Kind == SessionDateSettingKind.DateFormat);
        Assert.Contains(findings, f => f.Kind == SessionDateSettingKind.DateFirst);
    }

    [Fact]
    public void UnrelatedSetOption_NeverFires()
    {
        var findings = Scan("SET NOCOUNT ON; SET ANSI_NULLS ON;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NoSetStatement_NeverFires()
    {
        var findings = Scan("SELECT GETDATE();");

        Assert.Empty(findings);
    }
}
