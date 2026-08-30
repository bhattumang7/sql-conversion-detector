using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

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

        var view = View("CREATE VIEW dbo.V AS SELECT * FROM dbo.Base b JOIN dbo.Other o ON b.Id = o.Id;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A"]));
        catalog.AddViewCompiledColumns("dbo.V", ["Id", "A"]);

        Assert.Empty(StaleSelectStarViewScanner.Scan([view], catalog));
    }

    [Fact]
    public void ViewSelectsFromOwnCteSharingNameWithUnrelatedRealTable_NeverMisattributed()
    {

        var view = View("CREATE VIEW dbo.V AS WITH Base AS (SELECT Id, X FROM dbo.Other) SELECT * FROM Base;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A"]));
        catalog.AddViewCompiledColumns("dbo.V", ["Id", "X"]);

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

        var view = View("CREATE VIEW dbo.V AS SELECT * FROM dbo.Base;");
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Base", ["Id", "A"]));

        Assert.Empty(StaleSelectStarViewScanner.Scan([view], catalog));
    }

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

        var finding = Assert.Single(report.Find<StaleSelectStarViewFinding>("StaleSelectStarViewScanner"));
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

        Assert.Empty(report.Find<StaleSelectStarViewFinding>("StaleSelectStarViewScanner"));
    }
}
