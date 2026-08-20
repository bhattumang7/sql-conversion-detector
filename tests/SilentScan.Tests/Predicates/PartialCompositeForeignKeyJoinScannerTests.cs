using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Join predicate incomplete vs. the backing foreign key" -
/// live-mode only (<see cref="DatabaseCatalog.ForeignKeys"/> is only ever populated by
/// <c>LiveCatalogReader</c>), so like <see cref="CrossTableTypeDriftScannerTests"/> these tests
/// build the catalog directly to exercise the scanner's join-predicate-matching logic without
/// needing the Docker oracle for every case - the row-multiplication mechanism itself is proven
/// separately, with real seeded data, in <see cref="PartialCompositeForeignKeyJoinOracleTests"/>.
/// </summary>
public sealed class PartialCompositeForeignKeyJoinScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns, IReadOnlyList<CatalogIndex>? indexes = null) =>
        new(schema, name, CatalogTableKind.Table, columns, indexes ?? [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn Col(string name, SqlTypeCategory category = SqlTypeCategory.Int) =>
        new(name, new SqlType(category), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false);

    /// <summary>
    /// Orders(OrderId, RevisionId) with a composite PK/unique index on both columns, and
    /// OrderLines(LineId, OrderId, RevisionId) with a composite FK to it - the SCD2/multi-tenant
    /// bug shape (Kimball's own literature on effective-dated dimension joins, and the widely
    /// documented multi-tenant "always join on tenant_id too" SaaS bug class, both independently
    /// describe exactly this mechanism: omitting one column of a composite key fans a single
    /// child row out across every sibling row the omitted column would otherwise have excluded).
    /// <paramref name="extraOrdersIndexes"/> lets a test add a narrower unique index on Orders
    /// (the suppression-guard case) without duplicating the whole table shape.
    /// </summary>
    private static DatabaseCatalog BuildCatalog(IReadOnlyList<CatalogIndex>? extraOrdersIndexes = null)
    {
        var ordersIndexes = new List<CatalogIndex>
        {
            new("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, KeyColumns: ["OrderId", "RevisionId"], IncludedColumns: []),
        };
        if (extraOrdersIndexes is not null)
        {
            ordersIndexes.AddRange(extraOrdersIndexes);
        }

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("RevisionId")], ordersIndexes));
        catalog.AddOrReplace(Table("dbo", "OrderLines", [Col("LineId"), Col("OrderId"), Col("RevisionId")]));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_OrderLines_Orders", "dbo.OrderLines", "OrderId", "dbo.Orders", "OrderId"));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_OrderLines_Orders", "dbo.OrderLines", "RevisionId", "dbo.Orders", "RevisionId"));
        return catalog;
    }

    private static IReadOnlyList<PartialCompositeForeignKeyJoinFinding> Scan(string sql, DatabaseCatalog catalog)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var compositeForeignKeys = PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog);
        return PartialCompositeForeignKeyJoinScanner.Scan(result, catalog, compositeForeignKeys);
    }

    [Fact]
    public void BuildCompositeForeignKeys_ExcludesSingleColumnForeignKeys()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("CustomerId")]));
        catalog.AddOrReplace(Table("dbo", "Customers", [Col("CustomerId")]));
        catalog.AddForeignKey(new ForeignKeyRelationship("FK_Single", "dbo.Orders", "CustomerId", "dbo.Customers", "CustomerId"));

        var composite = PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog);

        Assert.Empty(composite);
    }

    [Fact]
    public void BuildCompositeForeignKeys_KeepsCompositeForeignKeys()
    {
        var catalog = BuildCatalog();

        var composite = PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog);

        var fk = Assert.Single(composite);
        Assert.Equal("FK_OrderLines_Orders", fk.ConstraintName);
        Assert.Equal(2, fk.Pairs.Count);
    }

    [Fact]
    public void JoinOnOnlyOneOfTwoCompositeColumns_Fires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId;", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("FK_OrderLines_Orders", finding.ConstraintName);
        Assert.Single(finding.MatchedColumnPairs);
        Assert.Equal("OrderId", finding.MatchedColumnPairs[0].ParentColumnName);
        Assert.Single(finding.MissingColumnPairs);
        Assert.Equal("RevisionId", finding.MissingColumnPairs[0].ParentColumnName);
    }

    [Fact]
    public void CteSharesNameWithReferencedTable_JoinNeverFires()
    {
        // 2026-08 audit: the ANSI-JOIN path's own ResolveDirectBaseTable independently re-
        // qualified and looked up each join side against the CATALOG directly, bypassing
        // FromScopeResolver's already-CTE-aware scope entirely - so a CTE named the same as
        // dbo.Orders (built here over dbo.OrderLines instead, meaning it can never have a real
        // FK relationship with dbo.OrderLines at all) silently resolved as if it WERE dbo.Orders,
        // firing a partial-composite-FK finding about a join the query never actually performs
        // against dbo.Orders. A CTE is never schema-qualified, so it always shadows a same-named
        // real base table - correctly resolved, the join side has no QualifiedName at all (a CTE
        // relation carries none), so ResolveDirectBaseTable must decline it, same as any other
        // non-base-table reference (a view, a derived table).
        var catalog = BuildCatalog();
        var findings = Scan(
            "WITH Orders AS (SELECT LineId AS OrderId FROM dbo.OrderLines) " +
            "SELECT 1 FROM dbo.OrderLines ol JOIN Orders o ON ol.OrderId = o.OrderId;",
            catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void JoinOnBothCompositeColumns_NeverFires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId AND ol.RevisionId = o.RevisionId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void MissingColumnCoveredSeparatelyInWhereClause_NeverFires()
    {
        // A composite key legitimately split across a JOIN's ON and a WHERE-clause filter (e.g.
        // a caller-supplied @RevisionId parameter) is not a bug - the missing pair is covered
        // elsewhere in the same statement.
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId WHERE ol.RevisionId = o.RevisionId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void MissingColumnMatchedAgainstAnUnrelatedThirdTable_StillFires()
    {
        var catalog = BuildCatalog();
        catalog.AddOrReplace(Table("dbo", "Audit", [Col("OrderId"), Col("RevisionId")]));
        var findings = Scan(
            """
            SELECT 1 FROM dbo.OrderLines ol
            JOIN dbo.Orders o ON ol.OrderId = o.OrderId
            JOIN dbo.Audit a ON ol.RevisionId = a.RevisionId AND ol.OrderId = a.OrderId;
            """, catalog);

        // The FK's own join (OrderLines/Orders) is still only ON OrderId - but RevisionId is
        // matched against Orders somewhere else in the statement? No: here it's matched between
        // OrderLines and Audit, not Orders - so this must still fire, since "covered anywhere"
        // requires the SAME table pair the FK actually connects, not just any equality
        // predicate mentioning the column name. Guards against a naive text-level "is RevisionId
        // mentioned anywhere" check that would wrongly suppress this.
        var finding = Assert.Single(findings);
        Assert.Equal("RevisionId", finding.MissingColumnPairs[0].ParentColumnName);
    }

    [Fact]
    public void UsedColumnSubsetCoveredByItsOwnUniqueIndexOnReferencedSide_Suppressed()
    {
        // Orders also carries a narrower unique index on OrderId alone (e.g. a natural key) -
        // joining on OrderId alone can never multiply rows regardless of what the FK's own
        // remaining column would have added, so this must not fire even though it's a genuine
        // partial-composite-FK join by the letter of the rule.
        var catalog = BuildCatalog(extraOrdersIndexes:
        [
            new CatalogIndex("UX_Orders_OrderId", CatalogIndexKind.UniqueConstraint, IsUnique: true, KeyColumns: ["OrderId"], IncludedColumns: []),
        ]);

        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void JoinMatchingNoneOfTheForeignKeyColumns_NeverFires()
    {
        // Zero overlap with the FK at all - "you didn't use the FK" is a different, much lower-
        // precision claim this stream deliberately does not make (see the finding's own doc
        // comment and the checklist's own scope note).
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.LineId = o.OrderId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void LegacyCommaJoinOnOnlyOneColumn_Fires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol, dbo.Orders o WHERE ol.OrderId = o.OrderId;", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("RevisionId", finding.MissingColumnPairs[0].ParentColumnName);
    }

    [Fact]
    public void LegacyCommaJoinOnBothColumns_NeverFires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol, dbo.Orders o WHERE ol.OrderId = o.OrderId AND ol.RevisionId = o.RevisionId;", catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateFromJoinOnOnlyOneColumn_Fires()
    {
        var catalog = BuildCatalog();
        var findings = Scan(
            "UPDATE ol SET ol.LineId = ol.LineId FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId;", catalog);

        var finding = Assert.Single(findings);
        Assert.Equal("RevisionId", finding.MissingColumnPairs[0].ParentColumnName);
    }

    [Fact]
    public void NoCompositeForeignKeysInCatalog_ScanShortCircuitsToEmpty()
    {
        var catalog = new DatabaseCatalog();
        var result = SqlScriptParser.ParseText("test.sql", "SELECT 1 FROM dbo.A a JOIN dbo.B b ON a.X = b.X;");
        Assert.False(result.HasErrors);

        var findings = PartialCompositeForeignKeyJoinScanner.Scan(result, catalog, PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys(catalog));

        Assert.Empty(findings);
    }

    [Fact]
    public void JoinAgainstUnrelatedThirdTable_NeverFires()
    {
        // No FK at all between OrderLines and this third table - must never fire regardless of
        // how many/few columns the join equates.
        var catalog = BuildCatalog();
        catalog.AddOrReplace(Table("dbo", "Customers", [Col("OrderId")]));
        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Customers c ON ol.OrderId = c.OrderId;", catalog);

        Assert.Empty(findings);
    }
}
