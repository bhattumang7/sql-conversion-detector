using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Lineage;

/// <summary>
/// Covers FromScopeResolver.ResolveTvfTableReference's CatalogTableKind.ClrTableValuedFunction
/// fallback: a live scan-db run has no sys.sql_modules body to parse for a SQLCLR (assembly)
/// TVF - there is no DDL text for it at all, even in principle - so LiveCatalogReader instead
/// registers its return-table shape straight from sys.columns metadata. This locks in that
/// FromScopeResolver actually consults that catalog entry as a fallback once the ordinary
/// inline/multi-statement TVF lookup misses, rather than leaving every column drawn from a CLR
/// TVF permanently untypeable the way it was before this fallback existed. CatalogBuilder never
/// produces a ClrTableValuedFunction entry itself (only LiveCatalogReader does, from live
/// metadata) - built here by hand, then merged into the catalog, exactly as LiveCatalogReader
/// would.
/// </summary>
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
        // Sanity check that the fallback is genuinely conditional: a TVF name with no catalog
        // entry AT ALL (never resolved as inline/multi-statement, never registered as a CLR
        // shape either) still declines exactly as before this fallback existed - never a guess.
        var lineage = BuildLineageWithClrTvf(
            new CatalogTable("dbo", "Split", CatalogTableKind.ClrTableValuedFunction, [], [], "dbo.Split", 0),
            "CREATE VIEW dbo.vw_Unknown AS SELECT u.x FROM dbo.NotRegisteredTvf('a') AS u;");

        Assert.Contains(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "FROM table-valued function" && e.Reason.Contains("dbo.NotRegisteredTvf", StringComparison.Ordinal));
    }
}
