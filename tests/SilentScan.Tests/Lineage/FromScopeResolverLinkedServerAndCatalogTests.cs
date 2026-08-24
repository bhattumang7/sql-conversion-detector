using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Lineage;

public sealed class FromScopeResolverLinkedServerAndCatalogTests
{
    private static LineageCatalog BuildLineage(params string[] batches)
    {
        var sql = string.Join("\nGO\n", batches);
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return LineageResolver.Resolve(catalog, [result]);
    }

    [Fact]
    public void LinkedServerTableValuedFunction_LedgersUnsupportedCrossServerReference()
    {
        var lineage = BuildLineage(
            "CREATE VIEW dbo.vw_RemoteTvf AS SELECT r.x FROM RemoteSrv.SomeDb.dbo.SomeTvf('a') AS r;");

        Assert.Contains(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "FROM table-valued function" && e.Reason.Contains("names a linked server", StringComparison.Ordinal));

        var view = lineage.Find("dbo.vw_RemoteTvf")!;
        Assert.IsType<ColumnProvenance.Unknown>(view.FindColumn("x")!.Provenance);
    }

    [Fact]
    public void SysTables_ResolvesTypedColumnsFromSystemCatalogRegistry_WithoutLedgering()
    {
        var lineage = BuildLineage(
            "CREATE VIEW dbo.vw_Tables AS SELECT name, object_id FROM sys.tables;");

        Assert.DoesNotContain(
            lineage.Skipped.Entries,
            e => e.Reason.Contains("sys.tables", StringComparison.OrdinalIgnoreCase));

        var view = lineage.Find("dbo.vw_Tables")!;
        var name = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("name")!.Provenance);
        Assert.Equal(SqlTypeCategory.NVarChar, name.Type!.Category);
        Assert.Equal(128, name.Type!.Length);

        var objectId = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("object_id")!.Provenance);
        Assert.Equal(SqlTypeCategory.Int, objectId.Type!.Category);
    }

    [Fact]
    public void DerivedTableExplicitColumnList_MatchingCount_RenamesToDeclaredNames()
    {
        var lineage = BuildLineage(
            "CREATE TABLE dbo.T (a INT NOT NULL, b VARCHAR(10) NOT NULL);",
            "CREATE VIEW dbo.vw_Renamed AS SELECT d.x, d.y FROM (SELECT a, b FROM dbo.T) AS d(x, y);");

        Assert.DoesNotContain(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "derived table column list");

        var view = lineage.Find("dbo.vw_Renamed")!;
        var x = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("x")!.Provenance);
        Assert.Equal("a", x.ColumnName);
        Assert.Equal(SqlTypeCategory.Int, x.Type!.Category);

        var y = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("y")!.Provenance);
        Assert.Equal("b", y.ColumnName);
        Assert.Equal(SqlTypeCategory.VarChar, y.Type!.Category);
    }

    [Fact]
    public void DerivedTableExplicitColumnList_CountMismatch_DegradesEveryColumnToUnknown()
    {
        var lineage = BuildLineage(
            "CREATE TABLE dbo.T (a INT NOT NULL, b VARCHAR(10) NOT NULL);",
            "CREATE VIEW dbo.vw_Mismatch AS SELECT d.x, d.b FROM (SELECT a, b FROM dbo.T) AS d(x);");

        Assert.Contains(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "derived table column list"
                && e.Reason.Contains("declares 1 column name(s) but its query resolved 2", StringComparison.Ordinal));

        var view = lineage.Find("dbo.vw_Mismatch")!;
        Assert.IsType<ColumnProvenance.Unknown>(view.FindColumn("x")!.Provenance);
        Assert.IsType<ColumnProvenance.Unknown>(view.FindColumn("b")!.Provenance);
    }
}
