using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class MemoryOptimizedLedgerConflictScannerTests
{
    private static IReadOnlyList<MemoryOptimizedLedgerConflictFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return MemoryOptimizedLedgerConflictScanner.Scan(result);
    }

    [Fact]
    public void MemoryOptimizedAndLedger_BothOn_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Accounts (AccountId INT NOT NULL PRIMARY KEY NONCLUSTERED, Balance DECIMAL(19,4) NOT NULL) WITH (MEMORY_OPTIMIZED = ON, LEDGER = ON);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Accounts", finding.TableQualifiedName);
    }

    [Fact]
    public void MemoryOptimizedOnly_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Accounts (AccountId INT NOT NULL PRIMARY KEY NONCLUSTERED, Balance DECIMAL(19,4) NOT NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void LedgerOnly_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Accounts (AccountId INT NOT NULL PRIMARY KEY, Balance DECIMAL(19,4) NOT NULL) WITH (LEDGER = ON);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NeitherOption_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.Accounts (AccountId INT NOT NULL PRIMARY KEY, Balance DECIMAL(19,4) NOT NULL);
            """);

        Assert.Empty(findings);
    }
}
