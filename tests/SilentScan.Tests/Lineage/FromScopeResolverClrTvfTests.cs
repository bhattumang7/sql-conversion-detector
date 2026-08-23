using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Lineage;

public sealed class FromScopeResolverClrTvfTests
{
    private static LineageCatalog BuildLineageWithClrTvf(CatalogTable clrTvf, params string[] batches)
    {
        var sql = string.Join("\nGO\n", batches);
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        catalog.AddOrReplace(clrTvf);
        return LineageResolver.Resolve(catalog, [result]);
    }

    [Fact]
    public void ViewOverClrTvf_ResolvesReturnColumnsFromCatalogFallback()
    {
        var clrSplit = new CatalogTable(
            SchemaName: "dbo",
            Name: "Split",
            Kind: CatalogTableKind.ClrTableValuedFunction,
            Columns:
            [
                new CatalogColumn("idx", new SqlType(SqlTypeCategory.Int), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false),
                new CatalogColumn("value", new SqlType(SqlTypeCategory.NVarChar, IsMax: true), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false),
            ],
            Indexes: [],
            SourcePath: "dbo.Split",
            SourceLine: 0);

        var lineage = BuildLineageWithClrTvf(
            clrSplit,
            "CREATE VIEW dbo.vw_Split AS SELECT s.idx, s.value FROM dbo.Split('a,b,c', ',') AS s;");

        var view = lineage.Find("dbo.vw_Split")!;
        var idx = view.FindColumn("idx")!;
        var value = view.FindColumn("value")!;

        Assert.Equal(SqlTypeCategory.Int, ((ColumnProvenance.BaseColumn)idx.Provenance).Type!.Category);
        Assert.Equal(SqlTypeCategory.NVarChar, ((ColumnProvenance.BaseColumn)value.Provenance).Type!.Category);
        Assert.DoesNotContain(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "FROM table-valued function" && e.Reason.Contains("dbo.Split", StringComparison.Ordinal));
    }

    [Fact]
    public void ViewOverUnregisteredTvf_StillLedgersUnresolved()
    {

        var lineage = BuildLineageWithClrTvf(
            new CatalogTable("dbo", "Split", CatalogTableKind.ClrTableValuedFunction, [], [], "dbo.Split", 0),
            "CREATE VIEW dbo.vw_Unknown AS SELECT u.x FROM dbo.NotRegisteredTvf('a') AS u;");

        Assert.Contains(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "FROM table-valued function" && e.Reason.Contains("dbo.NotRegisteredTvf", StringComparison.Ordinal));
    }
}
