using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Lineage;

public sealed class FromScopeResolverTableVariableTests
{
    private static (IReadOnlyList<SargabilityFinding> Findings, SkipLedger Ledger) Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var ledger = new SkipLedger();
        var findings = NonSargablePredicateScanner.Scan(result, catalog, lineage, ledger: ledger);
        return (findings, ledger);
    }

    [Fact]
    public void DeclaredTableVariable_UsedInFromClause_ResolvesWithoutLedgeringMissingDeclare()
    {
        var (_, ledger) = Scan("""
            CREATE PROCEDURE dbo.UsesDeclaredVariable AS
            BEGIN
                DECLARE @t TABLE (Id INT NOT NULL, Amount INT NOT NULL);
                SELECT Id FROM @t WHERE YEAR(Amount) = 2020;
            END
            """);

        Assert.DoesNotContain(
            ledger.Entries,
            e => e.ConstructKind == "FROM table reference" && e.Reason.Contains("has no known DECLARE/RETURNS", StringComparison.Ordinal));
    }

    [Fact]
    public void UndeclaredTableVariable_UsedInFromClause_LedgersMissingDeclare()
    {
        var (_, ledger) = Scan("""
            CREATE PROCEDURE dbo.UsesUndeclaredVariable AS
            BEGIN
                SELECT Id FROM @missing WHERE YEAR(Amount) = 2020;
            END
            """);

        Assert.Contains(
            ledger.Entries,
            e => e.ConstructKind == "FROM table reference"
                && e.Reason.Contains("table variable '@missing' has no known DECLARE/RETURNS in scope", StringComparison.Ordinal));
    }
}
