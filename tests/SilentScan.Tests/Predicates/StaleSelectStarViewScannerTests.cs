using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "View defined with
/// SELECT * whose compiled column list has gone stale against the base table's current shape" -
/// see <see cref="StaleSelectStarViewFinding"/> for the full precision story and oracle evidence
/// (including the confirmed phantom-data-under-a-stale-label mechanism). Structural tests build
/// the view definition via the real <see cref="ViewDefinitionExtractor"/> (the same code path
/// production uses) but hand-set the catalog's view-compiled-columns registry directly, since that
/// registry is live-only by construction; the end-to-end oracle test proves the real
/// LiveCatalogReader read.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class StaleSelectStarViewScannerTests
{
    private static ViewDefinition View(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var (views, _) = ViewDefinitionExtractor.Extract([result], defaultCollation: null, typeAliases: null, ledger: null);
        return Assert.Single(views);
    }

    private static CatalogTable Table(string schema, string name, IReadOnlyList<string> columnNames) =>
        new(schema, name, CatalogTableKind.Table,
            [.. columnNames.Select(c => new CatalogColumn(c, new SqlType(SqlTypeCategory.Int), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false))],
            [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    [Fact]
    public void ViewColumnsMatchBaseTable_NeverFires()
    {
        var view = View("CREATE VIEW dbo.V AS SELECT * FROM dbo.Base;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A", "B"]));
        catalog.AddViewCompiledColumns("dbo.V", ["Id", "A", "B"]);

        Assert.Empty(StaleSelectStarViewScanner.Scan([view], catalog));
    }

    [Fact]
    public void BaseTableGainedColumn_Fires()
    {
        var view = View("CREATE VIEW dbo.V AS SELECT * FROM dbo.Base;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A", "B", "C"]));
        catalog.AddViewCompiledColumns("dbo.V", ["Id", "A", "B"]);

        var finding = Assert.Single(StaleSelectStarViewScanner.Scan([view], catalog));
        Assert.Equal("dbo.V", finding.ViewQualifiedName);
        Assert.Equal("dbo.Base", finding.BaseTableQualifiedName);
        Assert.Equal(["Id", "A", "B"], finding.ViewCompiledColumns);
        Assert.Equal(["Id", "A", "B", "C"], finding.BaseTableCurrentColumns);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void DropThenAddShiftsIdentity_StillFires()
    {
        // The confirmed phantom-data shape: same column COUNT, different identity at the same
        // ordinal position - a naive set-equality check would miss this.
        var view = View("CREATE VIEW dbo.V AS SELECT * FROM dbo.Base;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A", "C"]));
        catalog.AddViewCompiledColumns("dbo.V", ["Id", "A", "B"]);

        var finding = Assert.Single(StaleSelectStarViewScanner.Scan([view], catalog));
        Assert.Equal(["Id", "A", "B"], finding.ViewCompiledColumns);
        Assert.Equal(["Id", "A", "C"], finding.BaseTableCurrentColumns);
    }

    [Fact]
    public void ViewOverJoin_NeverFires()
    {
        // Deliberate v1 scope limit - only a single, real, named base table qualifies.
        var view = View("CREATE VIEW dbo.V AS SELECT * FROM dbo.Base b JOIN dbo.Other o ON b.Id = o.Id;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A"]));
        catalog.AddViewCompiledColumns("dbo.V", ["Id", "A"]);

        Assert.Empty(StaleSelectStarViewScanner.Scan([view], catalog));
    }

    [Fact]
    public void ViewWithExplicitColumnList_NeverFires()
    {
        var view = View("CREATE VIEW dbo.V AS SELECT Id, A FROM dbo.Base;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A", "B"]));
        catalog.AddViewCompiledColumns("dbo.V", ["Id", "A"]);

        Assert.Empty(StaleSelectStarViewScanner.Scan([view], catalog));
    }

    [Fact]
    public void ViewCompiledColumnsUnknown_NeverGuesses()
    {
        // File-mode/no live catalog entry - never guessed, matching this codebase's own "never
        // guess" discipline for a live-only catalog fact.
        var view = View("CREATE VIEW dbo.V AS SELECT * FROM dbo.Base;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A"]));

        Assert.Empty(StaleSelectStarViewScanner.Scan([view], catalog));
    }

    /// <summary>
    /// End-to-end against the real standing Docker oracle (a fresh, disposable database, dropped
    /// unconditionally afterward): a base-table ADD COLUMN after the view was created leaves the
    /// view's own compiled column list stale, exactly as oracle-confirmed manually.
    /// </summary>
    [Fact]
    public async Task LiveDeployment_BaseTableGainsColumnAfterViewCreated_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.StaleViewBase (Id INT NOT NULL PRIMARY KEY, A INT NULL);
            GO
            CREATE VIEW dbo.StaleViewOverBase AS SELECT * FROM dbo.StaleViewBase;
            GO
            ALTER TABLE dbo.StaleViewBase ADD B INT NULL;
            """);

        var finding = Assert.Single(report.StaleSelectStarViewFindings);
        Assert.Equal("dbo.StaleViewOverBase", finding.ViewQualifiedName);
        Assert.Equal("dbo.StaleViewBase", finding.BaseTableQualifiedName);
        Assert.Equal(["Id", "A"], finding.ViewCompiledColumns);
        Assert.Equal(["Id", "A", "B"], finding.BaseTableCurrentColumns);
    }

    [Fact]
    public async Task LiveDeployment_ViewUnchangedAfterCreation_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.StaleViewCleanBase (Id INT NOT NULL PRIMARY KEY, A INT NULL);
            GO
            CREATE VIEW dbo.StaleViewCleanOverBase AS SELECT * FROM dbo.StaleViewCleanBase;
            """);

        Assert.Empty(report.StaleSelectStarViewFindings);
    }
}
