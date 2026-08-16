using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Temporal table history-side index gap".
/// <see cref="DatabaseCatalog.TemporalTablePairs"/> is only ever populated by live mode - these
/// tests build the catalog directly, the same pattern <c>UntrustedConstraintScannerTests</c>
/// already uses for the same reason. Both sides of a pair are ordinary <see cref="CatalogTable"/>
/// rows (a history table is not a distinct catalog object kind), so the tests construct them the
/// same way any other table/index fixture in this codebase does.
/// </summary>
public sealed class TemporalTableHistoryIndexGapScannerTests
{
    private static CatalogIndex Index(string? name, CatalogIndexKind kind, params string[] keyColumns) =>
        new(name, kind, IsUnique: false, keyColumns, IncludedColumns: [], IsFiltered: false, IsColumnstore: false, IsDisabled: false);

    private static CatalogTable Table(string schema, string name, params CatalogIndex[] indexes) =>
        new(schema, name, CatalogTableKind.Table, Columns: [], indexes, SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static DatabaseCatalog CatalogWithPair(CatalogTable current, CatalogTable history)
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(current);
        catalog.AddOrReplace(history);
        catalog.AddTemporalTablePair(new TemporalTablePair(current.QualifiedName, history.QualifiedName));
        return catalog;
    }

    [Fact]
    public void CurrentIndexMissingFromHistory_Fires()
    {
        var current = Table("dbo", "Widget", Index("IX_Widget_Code", CatalogIndexKind.Index, "Code"));
        var history = Table("dbo", "WidgetHistory", Index("ix_WidgetHistory", CatalogIndexKind.Index, "ValidTo", "ValidFrom"));
        var catalog = CatalogWithPair(current, history);

        var finding = Assert.Single(TemporalTableHistoryIndexGapScanner.Scan(catalog));
        Assert.Equal("dbo.Widget", finding.CurrentTableQualifiedName);
        Assert.Equal("dbo.WidgetHistory", finding.HistoryTableQualifiedName);
        Assert.Equal("IX_Widget_Code", finding.CurrentIndexName);
        Assert.Equal(["Code"], finding.KeyColumns);
    }

    [Fact]
    public void MatchingHistoryIndex_SameKeyColumnsSameOrder_NeverFires()
    {
        var current = Table("dbo", "Widget", Index("IX_Widget_Code", CatalogIndexKind.Index, "Code"));
        var history = Table("dbo", "WidgetHistory", Index("IX_WidgetHistory_Code", CatalogIndexKind.Index, "Code"));
        var catalog = CatalogWithPair(current, history);

        Assert.Empty(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void MatchingHistoryIndex_IgnoresIndexNameAndIncludedColumns()
    {
        var current = Table("dbo", "Widget",
            new CatalogIndex("IX_Widget_Code", CatalogIndexKind.Index, IsUnique: false, ["Code"], IncludedColumns: ["Name"], IsFiltered: false, IsColumnstore: false, IsDisabled: false));
        var history = Table("dbo", "WidgetHistory",
            new CatalogIndex("some_other_name", CatalogIndexKind.Index, IsUnique: false, ["Code"], IncludedColumns: [], IsFiltered: false, IsColumnstore: false, IsDisabled: false));
        var catalog = CatalogWithPair(current, history);

        Assert.Empty(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void ReversedKeyColumnOrderOnHistorySide_StillFires()
    {
        // Oracle-decided (TemporalTableHistoryIndexGapFinding's own doc comment): key-column
        // order is treated as significant even though a reversed order can still allow a seek
        // for a full-equality-on-every-column predicate - the conservative, structurally-safe
        // reading for a finding that makes no claim about any one query's own predicate shape.
        var current = Table("dbo", "WidgetB", Index("IX_WidgetB_Region_Code", CatalogIndexKind.Index, "Region", "Code"));
        var history = Table("dbo", "WidgetBHistory", Index("IX_WidgetBHistory_Code_Region", CatalogIndexKind.Index, "Code", "Region"));
        var catalog = CatalogWithPair(current, history);

        var finding = Assert.Single(TemporalTableHistoryIndexGapScanner.Scan(catalog));
        Assert.Equal(["Region", "Code"], finding.KeyColumns);
    }

    [Fact]
    public void PrimaryKeyIndex_NeverCompared()
    {
        // Oracle-confirmed structurally impossible on a history table (Msg 13558) - a PK-kind
        // current-side index must never be a candidate, or this would always fire with no
        // possible fix.
        var current = Table("dbo", "Widget", Index("PK_Widget", CatalogIndexKind.PrimaryKey, "WidgetId"));
        var history = Table("dbo", "WidgetHistory", Index("ix_WidgetHistory", CatalogIndexKind.Index, "ValidTo", "ValidFrom"));
        var catalog = CatalogWithPair(current, history);

        Assert.Empty(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void UniqueConstraintIndex_NeverCompared()
    {
        // Oracle-confirmed structurally impossible on a history table (Msg 13583), same reasoning
        // as the PRIMARY KEY case above.
        var current = Table("dbo", "Widget", Index("UQ_Widget_Code", CatalogIndexKind.UniqueConstraint, "Code"));
        var history = Table("dbo", "WidgetHistory");
        var catalog = CatalogWithPair(current, history);

        Assert.Empty(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void FilteredHistoryIndex_NotTreatedAsAMatch()
    {
        var current = Table("dbo", "Widget", Index("IX_Widget_Code", CatalogIndexKind.Index, "Code"));
        var history = Table("dbo", "WidgetHistory",
            new CatalogIndex("IX_WidgetHistory_Code", CatalogIndexKind.Index, IsUnique: false, ["Code"], IncludedColumns: [], IsFiltered: true, IsColumnstore: false, IsDisabled: false));
        var catalog = CatalogWithPair(current, history);

        Assert.Single(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void DisabledHistoryIndex_NotTreatedAsAMatch()
    {
        var current = Table("dbo", "Widget", Index("IX_Widget_Code", CatalogIndexKind.Index, "Code"));
        var history = Table("dbo", "WidgetHistory",
            new CatalogIndex("IX_WidgetHistory_Code", CatalogIndexKind.Index, IsUnique: false, ["Code"], IncludedColumns: [], IsFiltered: false, IsColumnstore: false, IsDisabled: true));
        var catalog = CatalogWithPair(current, history);

        Assert.Single(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void DisabledCurrentIndex_NeverACandidate()
    {
        var current = Table("dbo", "Widget",
            new CatalogIndex("IX_Widget_Code", CatalogIndexKind.Index, IsUnique: false, ["Code"], IncludedColumns: [], IsFiltered: false, IsColumnstore: false, IsDisabled: true));
        var history = Table("dbo", "WidgetHistory");
        var catalog = CatalogWithPair(current, history);

        Assert.Empty(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void ColumnstoreCurrentIndex_NeverACandidate()
    {
        var current = Table("dbo", "Widget",
            new CatalogIndex("CCI_Widget", CatalogIndexKind.Index, IsUnique: false, [], IncludedColumns: [], IsFiltered: false, IsColumnstore: true, IsDisabled: false));
        var history = Table("dbo", "WidgetHistory");
        var catalog = CatalogWithPair(current, history);

        Assert.Empty(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void UnresolvedTableInPair_SkippedRatherThanThrowing()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddTemporalTablePair(new TemporalTablePair("dbo.Ghost", "dbo.GhostHistory"));

        Assert.Empty(TemporalTableHistoryIndexGapScanner.Scan(catalog));
    }

    [Fact]
    public void MultipleMissingIndexes_OneFindingEach()
    {
        var current = Table("dbo", "Widget",
            Index("IX_Widget_Code", CatalogIndexKind.Index, "Code"),
            Index("IX_Widget_Region", CatalogIndexKind.Index, "Region"));
        var history = Table("dbo", "WidgetHistory");
        var catalog = CatalogWithPair(current, history);

        var findings = TemporalTableHistoryIndexGapScanner.Scan(catalog);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.CurrentIndexName == "IX_Widget_Code");
        Assert.Contains(findings, f => f.CurrentIndexName == "IX_Widget_Region");
    }
}
