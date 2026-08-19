using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Hint and index-shape catalog checks": "Composite index
/// leading-column violation" - the b-tree-prefix mechanism itself needs no plan-XML oracle (it is
/// architectural, not cardinality-dependent), so a hand-built catalog exercises the scanner's own
/// matching/suppression logic directly, the same discipline
/// <see cref="PartialCompositeForeignKeyJoinScannerTests"/> already established for a catalog-only,
/// AST-driven rule.
/// </summary>
public sealed class CompositeIndexLeadingColumnScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns, IReadOnlyList<CatalogIndex> indexes) =>
        new(schema, name, CatalogTableKind.Table, columns, indexes, SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn Col(string name) => new(name, new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false);

    private static IReadOnlyList<CompositeIndexLeadingColumnFinding> Scan(string sql, DatabaseCatalog catalog)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return CompositeIndexLeadingColumnScanner.Scan(result, catalog);
    }

    private static DatabaseCatalog CatalogWithComposite(params CatalogIndex[] extraIndexes)
    {
        var indexes = new List<CatalogIndex>
        {
            new("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, KeyColumns: ["OrderId"], IncludedColumns: []),
            new("IX_Orders_Region_Status", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Region", "Status"], IncludedColumns: []),
        };
        indexes.AddRange(extraIndexes);

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("Region"), Col("Status")], indexes));
        return catalog;
    }

    [Fact]
    public void PredicateOnNonLeadingColumnOnly_LeadingUnconstrainedAnywhere_Fires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", CatalogWithComposite());

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal("IX_Orders_Region_Status", finding.IndexName);
        Assert.Equal("Status", finding.ViolatingColumnName);
        Assert.Equal(1, finding.ViolatingColumnPosition);
    }

    [Fact]
    public void PredicateOnLeadingColumnToo_NeverFires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Region = 1 AND Status = 5;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void PredicateOnLeadingColumnWithDifferentCasingThanDdl_StillSuppresses()
    {
        // 2026-08 audit: the suppression set is keyed by the FROM-clause spelling
        // (ColumnProvenance.BaseColumn.TableQualifiedName), the lookup uses the catalog/DDL
        // spelling (CatalogTable.QualifiedName) - a bare HashSet default comparer treated
        // "DBO.Orders" and "dbo.Orders" as different tables, so a leading column genuinely bound
        // through a differently-cased reference fired a false violation.
        var findings = Scan("SELECT 1 FROM DBO.ORDERS WHERE Region = 1 AND Status = 5;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void CteSharesNameWithIndexedBaseTable_NeverBindsToTheBaseTable()
    {
        // 2026-08 audit: cteRelations was always null here, so a CTE named the same as a real
        // indexed base table silently resolved against the CATALOG table instead - firing a
        // violation about a table the query never actually reads. A CTE is never schema-
        // qualified, so it always shadows a same-named base table for its statement's lifetime;
        // resolving through the CTE correctly yields no real base table at all (a CTE relation
        // has no QualifiedName), so this scanner - which only ever reasons about real base
        // tables - must decline the whole statement, not report against dbo.Orders.
        var findings = Scan(
            "WITH Orders AS (SELECT 1 AS Region, 2 AS Status) SELECT 1 FROM Orders WHERE Status = 5;",
            CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void LeadingColumnReferencedOnlyInsideOrBranch_StillSuppresses()
    {
        // Conservative by design: even a weak, OR-reachable reference to the leading column is
        // enough to decline - this set is liberal on purpose, only ever used to suppress.
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5 AND (Region = 1 OR Region = 2);", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void PredicateOnNonLeadingColumnOnlyReachableThroughOr_NeverFires()
    {
        // The violating column itself must be AND-reachable to trigger - an OR-only reference
        // doesn't guarantee the column is ever actually bound.
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5 OR OrderId = 1;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void AlternativeIndexLeadsWithTheSameViolatingColumn_Suppresses()
    {
        var extra = new CatalogIndex("IX_Orders_Status", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Status"], IncludedColumns: []);
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", CatalogWithComposite(extra));

        Assert.Empty(findings);
    }

    [Fact]
    public void SingleColumnIndex_NeverConsidered()
    {
        // OrderId's own PK index is single-column - nothing here is "composite", so it can never
        // be a candidate regardless of what the query constrains.
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE OrderId = 5;", CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void FilteredIndex_NeverConsidered()
    {
        var filtered = new CatalogIndex("IX_Orders_Region_Status_Filtered", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Region", "Status"], IncludedColumns: [], IsFiltered: true);
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("Region"), Col("Status")], [filtered]));

        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void JoinOnClauseSuppliesTheLeadingColumn_Suppresses()
    {
        var catalog = CatalogWithComposite();
        catalog.AddOrReplace(Table("dbo", "Regions", [Col("RegionId")], []));

        var findings = Scan(
            "SELECT 1 FROM dbo.Orders o JOIN dbo.Regions r ON o.Region = r.RegionId WHERE o.Status = 5;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateStatement_Fires()
    {
        var findings = Scan("UPDATE dbo.Orders SET OrderId = OrderId WHERE Status = 5;", CatalogWithComposite());

        Assert.Single(findings);
    }

    [Fact]
    public void DeleteStatement_Fires()
    {
        var findings = Scan("DELETE FROM dbo.Orders WHERE Status = 5;", CatalogWithComposite());

        Assert.Single(findings);
    }

    [Fact]
    public void WildcardColumnReferenceInsideNestedSubquery_NeverCrashes()
    {
        // Oracle-found against real corpus text: a COUNT(*) nested inside a scalar subquery's own
        // WHERE clause reaches the liberal ColumnReferenceCollector, whose wildcard argument has
        // no MultiPartIdentifier at all - this must be skipped, never crash.
        var findings = Scan(
            """
            SELECT 1 FROM dbo.Orders
            WHERE Region = 1 AND Status = 5
              AND OrderId = (SELECT TOP 1 OrderId FROM dbo.Orders WHERE (SELECT COUNT(*) FROM dbo.Orders) > 0);
            """,
            CatalogWithComposite());

        Assert.Empty(findings);
    }

    [Fact]
    public void ThreeColumnIndex_ViolatingColumnAtLaterPosition_ReportsCorrectPosition()
    {
        var threeCol = new CatalogIndex("IX_Orders_A_B_C", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Region", "Status", "OrderId"], IncludedColumns: []);
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("Region"), Col("Status")], [threeCol]));

        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE OrderId = 5;", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("OrderId", finding.ViolatingColumnName);
        Assert.Equal(2, finding.ViolatingColumnPosition);
    }
}
