using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NonSargablePredicateScannerCallerScopeTests
{
    private static SkipLedger Scan(string sql, IReadOnlyDictionary<string, IReadOnlyList<string>> callerScopeByCalleeScope)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var ledger = new SkipLedger();
        NonSargablePredicateScanner.Scan(result, catalog, lineage, ledger: ledger, callerScopeByCalleeScope: callerScopeByCalleeScope);
        return ledger;
    }

    private static bool HasNoKnownDdlEntry(SkipLedger ledger) =>
        ledger.Entries.Any(e => e.ConstructKind == "FROM table reference" && e.Reason.Contains("#Staging", StringComparison.Ordinal) && e.Reason.Contains("has no known DDL", StringComparison.Ordinal));

    [Fact]
    public void CalleeReferencesCallersTempTable_ResolvesThroughSingleCallerScope()
    {
        var ledger = Scan("""
            CREATE PROCEDURE dbo.CallerProc AS
            BEGIN
                CREATE TABLE #Staging (Id INT NOT NULL, Amount TINYINT NOT NULL);
            END
            GO
            CREATE PROCEDURE dbo.CalleeProc AS
            BEGIN
                SELECT Id FROM #Staging WHERE Amount = 1;
            END
            """,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.CalleeProc"] = ["dbo.CallerProc"],
            });

        Assert.False(HasNoKnownDdlEntry(ledger));
    }

    [Fact]
    public void CalleeReferencesTempTable_DeclaredWithDifferentShapesAcrossCallers_DeclinesRatherThanGuesses()
    {
        var ledger = Scan("""
            CREATE PROCEDURE dbo.CallerA AS
            BEGIN
                CREATE TABLE #Staging (Id INT NOT NULL, Amount TINYINT NOT NULL);
            END
            GO
            CREATE PROCEDURE dbo.CallerB AS
            BEGIN
                CREATE TABLE #Staging (Id INT NOT NULL, Name VARCHAR(10) NOT NULL);
            END
            GO
            CREATE PROCEDURE dbo.CalleeProc AS
            BEGIN
                SELECT Id FROM #Staging WHERE Amount = 1;
            END
            """,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.CalleeProc"] = ["dbo.CallerA", "dbo.CallerB"],
            });

        Assert.True(HasNoKnownDdlEntry(ledger));
    }
}
