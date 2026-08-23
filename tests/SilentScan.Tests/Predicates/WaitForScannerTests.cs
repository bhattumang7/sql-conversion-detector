using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class WaitForScannerTests
{
    private static IReadOnlyList<WaitForFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return WaitForScanner.Scan(result);
    }

    [Fact]
    public void WaitForDelay_Fires()
    {
        var findings = Scan("WAITFOR DELAY '00:00:05';");

        var finding = Assert.Single(findings);
        Assert.False(finding.IsInsideTransaction);
    }

    [Fact]
    public void WaitForTime_Fires()
    {
        var findings = Scan("WAITFOR TIME '23:00:00';");

        Assert.Single(findings);
    }

    [Fact]
    public void WaitForDelay_InsideOpenTransaction_FlagsInsideTransaction()
    {
        var findings = Scan("BEGIN TRANSACTION; WAITFOR DELAY '00:00:05'; COMMIT TRANSACTION;");

        var finding = Assert.Single(findings);
        Assert.True(finding.IsInsideTransaction);
    }

    [Fact]
    public void WaitForDelay_AfterTransactionCommitted_NotFlaggedInsideTransaction()
    {
        var findings = Scan("BEGIN TRANSACTION; SELECT 1; COMMIT TRANSACTION; WAITFOR DELAY '00:00:05';");

        var finding = Assert.Single(findings);
        Assert.False(finding.IsInsideTransaction);
    }

    [Fact]
    public void WaitForDelay_InsideRolledBackTransaction_FlagsInsideTransaction()
    {
        var findings = Scan("BEGIN TRANSACTION; WAITFOR DELAY '00:00:05'; ROLLBACK TRANSACTION;");

        var finding = Assert.Single(findings);
        Assert.True(finding.IsInsideTransaction);
    }

    [Fact]
    public void NoWaitFor_NeverFires()
    {
        var findings = Scan("SELECT 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void WaitForReceive_NeverFires()
    {
        var findings = Scan("WAITFOR (RECEIVE TOP(1) * FROM dbo.SomeQueue), TIMEOUT 5000;");

        Assert.Empty(findings);
    }
}
