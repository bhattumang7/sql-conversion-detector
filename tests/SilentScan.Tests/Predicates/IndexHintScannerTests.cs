using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Hint and index-shape catalog checks": "Hint validity against the
/// catalog" - see <see cref="IndexHintFinding"/>/<see cref="IndexHintFindingKind"/> for the
/// oracle-confirmed mechanism behind each kind (Msg 308 for a nonexistent index; Index Scan
/// instead of Index Seek for an unbound leading column). Both are pure catalog+AST facts once
/// established, so a hand-built catalog exercises the scanner's own matching/suppression logic
/// directly, the same discipline <see cref="CompositeIndexLeadingColumnScannerTests"/> uses.
/// </summary>
public sealed class IndexHintScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns, IReadOnlyList<CatalogIndex> indexes) =>
        new(schema, name, CatalogTableKind.Table, columns, indexes, SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn Col(string name) => new(name, new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false);

    private static IReadOnlyList<IndexHintFinding> Scan(string sql, DatabaseCatalog catalog)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return IndexHintScanner.Scan(result, catalog);
    }

    private static DatabaseCatalog CatalogWithIndex()
    {
        var indexes = new List<CatalogIndex>
        {
            new("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, KeyColumns: ["OrderId"], IncludedColumns: []),
            new("IX_Orders_Status", CatalogIndexKind.Index, IsUnique: false, KeyColumns: ["Status"], IncludedColumns: []),
        };

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Col("OrderId"), Col("Status")], indexes));
        return catalog;
    }

    [Fact]
    public void HintNamesNonexistentIndex_Fires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WITH (INDEX(IX_DoesNotExist)) WHERE OrderId = 1;", CatalogWithIndex());

        var finding = Assert.Single(findings);
        Assert.Equal(IndexHintFindingKind.IndexDoesNotExist, finding.Kind);
        Assert.Equal("IX_DoesNotExist", finding.HintedIndexName);
        Assert.Null(finding.LeadingColumnName);
    }

    [Fact]
    public void HintNamesRealIndexWithUnboundLeadingColumn_Fires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WITH (INDEX(IX_Orders_Status)) WHERE OrderId = 1;", CatalogWithIndex());

        var finding = Assert.Single(findings);
        Assert.Equal(IndexHintFindingKind.HintedIndexNotSeekable, finding.Kind);
        Assert.Equal("IX_Orders_Status", finding.HintedIndexName);
        Assert.Equal("Status", finding.LeadingColumnName);
    }

    [Fact]
    public void HintNamesRealIndexWithBoundLeadingColumn_NeverFires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WITH (INDEX(IX_Orders_Status)) WHERE Status = 5;", CatalogWithIndex());

        Assert.Empty(findings);
    }

    [Fact]
    public void NoHints_NeverFires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE OrderId = 1;", CatalogWithIndex());

        Assert.Empty(findings);
    }

    [Fact]
    public void OrdinalIndexHint_DeclinedAsOutOfScope()
    {
        // INDEX(0) has no Identifier and no catalog name to resolve against - deliberately
        // declined rather than guessed at (see the finding's own doc comment).
        var findings = Scan("SELECT 1 FROM dbo.Orders WITH (INDEX(0)) WHERE OrderId = 1;", CatalogWithIndex());

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateStatementTargetHint_Fires()
    {
        // T-SQL only allows an index hint in a FROM or OPTION clause - an UPDATE naming a hint on
        // its own target needs the extended FROM form, oracle-confirmed via the real parser.
        var findings = Scan("UPDATE o SET OrderId = OrderId FROM dbo.Orders o WITH (INDEX(IX_Orders_Status)) WHERE OrderId = 1;", CatalogWithIndex());

        var finding = Assert.Single(findings);
        Assert.Equal(IndexHintFindingKind.HintedIndexNotSeekable, finding.Kind);
    }

    [Fact]
    public void HintOnJoinedTable_LeadingColumnBoundByJoinCondition_NeverFires()
    {
        var catalog = CatalogWithIndex();
        catalog.AddOrReplace(Table("dbo", "OrderLines", [Col("OrderId")], []));

        var findings = Scan(
            "SELECT 1 FROM dbo.OrderLines ol JOIN dbo.Orders o WITH (INDEX(IX_Orders_Status)) ON o.Status = ol.OrderId;", catalog);

        Assert.Empty(findings);
    }
}
