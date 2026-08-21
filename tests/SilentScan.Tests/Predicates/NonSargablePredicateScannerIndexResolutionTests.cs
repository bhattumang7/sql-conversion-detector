using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Covers <see cref="NonSargablePredicateScanner"/>'s catalog/lineage-aware overload directly:
/// a syntactic finding's <see cref="SargabilityFinding.TableQualifiedName"/>/
/// <see cref="SargabilityFinding.Indexed"/> must reflect the real catalog, not stay
/// permanently unresolved the way the no-catalog overload (still used by the plain fixture
/// tests) necessarily does.
/// </summary>
public sealed class NonSargablePredicateScannerIndexResolutionTests
{
    private static IReadOnlyList<SargabilityFinding> ScanWithCatalog(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return NonSargablePredicateScanner.Scan(result, catalog, lineage);
    }

    [Fact]
    public void FunctionWrappedColumn_OnLeadingKeyIndexedColumn_ResolvesIndexedTrue()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Orders (OrderDate DATETIME NOT NULL);
            CREATE INDEX IX_Orders_OrderDate ON dbo.Orders(OrderDate);
            GO
            SELECT 1 FROM dbo.Orders WHERE YEAR(OrderDate) = 2024;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void FunctionWrappedColumn_OnUnindexedColumn_ResolvesIndexedFalse()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Orders (Notes VARCHAR(200) NOT NULL);
            GO
            SELECT 1 FROM dbo.Orders WHERE UPPER(Notes) = 'X';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void FunctionWrappedColumn_InsideUnsatisfiableAndBranch_EliminatedByNormalization()
    {
        // Id=1 AND Id=2 can never select a row, so UPPER(Notes)='X' - an ordinary conjunct of
        // the same unsatisfiable AND - never reaches a real Filter/Seek decision either, even
        // though this scanner's own contradiction algebra never looks at wrapped-column
        // comparisons directly (it only has to prove the ENCLOSING AND is dead).
        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE TABLE dbo.Orders (Id INT NOT NULL, Notes VARCHAR(200) NOT NULL);
            GO
            SELECT 1 FROM dbo.Orders WHERE Id = 1 AND Id = 2 AND UPPER(Notes) = 'X';
            """);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var ledger = new SkipLedger();
        var findings = NonSargablePredicateScanner.Scan(result, catalog, lineage, ledger: ledger);

        Assert.Empty(findings);
        Assert.Contains(ledger.Entries, e => e.ConstructKind == "predicate eliminated by normalization");
    }

    [Fact]
    public void FunctionWrappedColumn_OnNonLeadingCompositeKeyColumn_ResolvesIndexedFalse()
    {
        // The column is technically a key column of an index, but not the LEADING one - it
        // can't drive a seek on its own, matching IndexDeploymentChecker's oracle precondition.
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL, Notes VARCHAR(200) NOT NULL, CONSTRAINT PK_Orders PRIMARY KEY (OrderId, Notes));
            GO
            SELECT 1 FROM dbo.Orders WHERE UPPER(Notes) = 'X';
            """);

        var finding = Assert.Single(findings);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void FunctionWrappedColumn_ReferencedThroughCteInsideInsertSelect_ResolvesIndexedTrue()
    {
        // The bug this closes: NonSargablePredicateScanner had no ExplicitVisit(InsertStatement)
        // override at all, unlike TypedPredicateExtractor's identical one - so a CTE declared on
        // an INSERT ... SELECT never got pushed, and a reference to it through the CTE alias
        // failed to resolve in FromScopeResolver. The syntactic finding still fired (InspectSide
        // works on the raw AST regardless of scope resolution), but Indexed silently resolved to
        // false/unresolved instead of true - understating the finding for ranking purposes.
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Orders (OrderDate DATETIME NOT NULL);
            CREATE INDEX IX_Orders_OrderDate ON dbo.Orders(OrderDate);
            GO
            CREATE TABLE dbo.OrdersArchive (OrderDate DATETIME NOT NULL);
            GO
            WITH RecentOrders AS (SELECT OrderDate FROM dbo.Orders)
            INSERT INTO dbo.OrdersArchive (OrderDate)
            SELECT OrderDate FROM RecentOrders WHERE YEAR(OrderDate) = 2024;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void FunctionWrappedColumn_OnUnresolvableTable_RecordsSkipInsteadOfSilence()
    {
        // The bug this closes: NonSargablePredicateScanner passed ledger: null to every shared
        // resolver it calls (FromScopeResolver/ScalarExpressionResolver/CteResolver) - the one
        // pass with zero trace of what it couldn't resolve, unlike every other pass in this
        // codebase. An unresolvable FROM table now leaves a real ledger entry, matching
        // FunctionWrappedColumn_OnUnresolvableTable_LeavesIndexedNull's existing Indexed=null
        // claim with an explicit reason instead of silence.
        var result = SqlScriptParser.ParseText("test.sql", "SELECT 1 FROM dbo.Missing WHERE UPPER(Notes) = 'X';");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        var ledger = new SkipLedger();
        var findings = NonSargablePredicateScanner.Scan(result, catalog, lineage, ledger: ledger);

        Assert.Single(findings);
        Assert.NotEmpty(ledger.Entries);
        Assert.Contains(ledger.Entries, e => e.Reason.Contains("dbo.Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionWrappedColumn_OnUnresolvableTable_LeavesIndexedNull()
    {
        // No CREATE TABLE for dbo.Missing anywhere in the scan - never guess.
        var findings = ScanWithCatalog("SELECT 1 FROM dbo.Missing WHERE UPPER(Notes) = 'X';");

        var finding = Assert.Single(findings);
        Assert.Null(finding.TableQualifiedName);
        Assert.Null(finding.Indexed);
    }

    [Fact]
    public void FunctionWrappedColumn_ThroughUpdateStatement_ResolvesAgainstTargetTable()
    {
        var findings = ScanWithCatalog("""
            CREATE TABLE dbo.Orders (Status VARCHAR(20) NOT NULL);
            CREATE INDEX IX_Orders_Status ON dbo.Orders(Status);
            GO
            UPDATE dbo.Orders SET Status = 'X' WHERE UPPER(Status) = 'OPEN';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.True(finding.Indexed);
    }

    [Fact]
    public void NoCatalogOverload_LeavesEveryFindingUnresolved()
    {
        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE TABLE dbo.Orders (OrderDate DATETIME NOT NULL);
            CREATE INDEX IX_Orders_OrderDate ON dbo.Orders(OrderDate);
            GO
            SELECT 1 FROM dbo.Orders WHERE YEAR(OrderDate) = 2024;
            """);

        var finding = Assert.Single(NonSargablePredicateScanner.Scan(result));

        Assert.Null(finding.TableQualifiedName);
        Assert.Null(finding.Indexed);
    }
}
