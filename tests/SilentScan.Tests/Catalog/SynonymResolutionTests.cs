using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Catalog;

/// <summary>
/// Regression coverage for synonym resolution (formerly pinned in
/// KnownGapCharacterizationTests.Synonym_IsNeverResolved_QueryThroughItYieldsNoTypedFinding):
/// CREATE SYNONYM is a pure name-&gt;name mapping (DatabaseCatalog.AddSynonym/ResolveSynonymName),
/// canonicalized at every FROM-clause reference before the catalog/view lookup
/// (FromScopeResolver), so a query through a synonym resolves - and reports - exactly like the
/// real base object, not the synonym name. Runs through <see cref="ScanReportBuilder"/>, the
/// same entry point production uses.
/// </summary>
public sealed class SynonymResolutionTests
{
    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task SynonymForTable_ResolvesToTheRealBaseTable()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL, INDEX IX_Sku (Sku));
            GO
            CREATE SYNONYM dbo.Stock FOR dbo.Inventory;
            GO
            SELECT 1 FROM dbo.Stock WHERE Sku = N'S1';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Sku");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Inventory", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task SynonymForView_Resolves_EvenThoughViewsAreNeverInDatabaseCatalog()
    {
        // The hardest case: a view is not in DatabaseCatalog at all (only LineageCatalog knows
        // about it), so a synonym pointing at one can only ever resolve through the
        // resolvedViews dictionary lookup, not catalog.Find - both must be canonicalized.
        var report = await Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL, INDEX IX_Sku (Sku));
            GO
            CREATE VIEW dbo.vInventory AS SELECT Sku FROM dbo.Inventory;
            GO
            CREATE SYNONYM dbo.StockView FOR dbo.vInventory;
            GO
            SELECT 1 FROM dbo.StockView WHERE Sku = N'S1';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Sku");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Inventory", finding.Column.TableQualifiedName);
        Assert.Equal(1, finding.Column.Depth);
    }

    [Fact]
    public async Task ViewDefinedOverSynonymForAnotherView_GetsACorrectDependencyEdge()
    {
        // Without threading synonym resolution into ViewDependencyGraph's own dependency-edge
        // collection, topological order could resolve vOuter before vInner and vOuter's Sku
        // column would degrade to Unknown regardless of the FromScopeResolver fix above.
        var report = await Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL, INDEX IX_Sku (Sku));
            GO
            CREATE VIEW dbo.vInner AS SELECT Sku FROM dbo.Inventory;
            GO
            CREATE SYNONYM dbo.SynInner FOR dbo.vInner;
            GO
            CREATE VIEW dbo.vOuter AS SELECT Sku FROM dbo.SynInner;
            GO
            SELECT 1 FROM dbo.vOuter WHERE Sku = N'S1';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Sku");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Inventory", finding.Column.TableQualifiedName);
    }

    [Fact]
    public async Task DropSynonym_MakesTheNameUnresolvedAgain()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Inventory (Sku varchar(40) NOT NULL);
            GO
            CREATE SYNONYM dbo.Stock FOR dbo.Inventory;
            GO
            DROP SYNONYM dbo.Stock;
            GO
            SELECT 1 FROM dbo.Stock WHERE Sku = N'S1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Contains(report.SkippedConstructs, s => s.Reason.Contains("dbo.Stock", StringComparison.Ordinal) && s.Reason.Contains("has no known DDL", StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds straight from parsed text via <see cref="CatalogBuilder"/>, never through
    /// <see cref="EngineAuthoritativeScan"/> - the two scenarios below are deliberately
    /// undeployable T-SQL (a real synonym cycle, a synonym targeting a linked server that does
    /// not exist), so a real SQL Server predictably REJECTS creating them outright (verified
    /// directly: "Synonym chaining is not allowed" / the linked server does not exist) rather
    /// than silently accepting and letting this pass's own cycle/ledger-safety logic run at
    /// all. These two are testing THIS PASS's own resilience to text a real corpus script might
    /// contain and ScriptDom parses fine, independent of whether that text could ever actually
    /// deploy - CatalogBuilder is still a live, used component (DatabaseCatalog.MergeFileModeExtras),
    /// so exercising it directly here is not testing a deleted code path.
    /// </summary>
    private static ScanReport ScanParsedOnly(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("synonym.sql", sql);
        Assert.Empty(parseResult.Errors);
        var catalog = CatalogBuilder.Build([parseResult]);
        return ScanReportBuilder.BuildFromParseResults([parseResult], catalog);
    }

    [Fact]
    public void SynonymCycle_FallsBackToTheOriginalNameRatherThanLooping()
    {
        // Invalid T-SQL (a real synonym can't target another synonym at all), but this pass
        // must never loop on a corpus script that tries it anyway - a cycle resolves to the
        // ORIGINAL input name, which then takes the ordinary honestly-ledgered "no known DDL"
        // path rather than a guess.
        var report = ScanParsedOnly("""
            CREATE SYNONYM dbo.A FOR dbo.B;
            GO
            CREATE SYNONYM dbo.B FOR dbo.A;
            GO
            SELECT 1 FROM dbo.A WHERE Sku = N'S1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Contains(report.SkippedConstructs, s => s.Reason.Contains("dbo.A", StringComparison.Ordinal));
    }

    [Fact]
    public void FourPartLinkedServerSynonym_IsLedgeredNotMisregistered()
    {
        // SchemaObjectNameHelper.Qualify silently drops ServerIdentifier - registering this
        // under "otherdb.dbo.RemoteInventory" (dropping "linkedserver") could alias an
        // unrelated LOCAL object sharing that same three-part tail. Must ledger, never register.
        var report = ScanParsedOnly("""
            CREATE SYNONYM dbo.RemoteStock FOR linkedserver.otherdb.dbo.RemoteInventory;
            GO
            SELECT 1 FROM dbo.RemoteStock WHERE Sku = N'S1';
            """);

        Assert.Empty(report.TypedFindings);
        Assert.Contains(report.SkippedConstructs, s => s.ConstructKind == "CREATE SYNONYM" && s.Reason.Contains("linked server", StringComparison.Ordinal));
    }
}
