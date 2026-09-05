using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Lineage;

public sealed class ColumnProvenanceNonDeterministicComputedColumnTests
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
    public void ColumnReferencingNonDeterministicComputedColumn_IsFlaggedNonDeterministic()
    {
        var lineage = BuildLineage(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL, CreatedAtLabel AS CONVERT(varchar(30), GETDATE()));",
            "CREATE VIEW dbo.vw_Orders AS SELECT o.Id, o.CreatedAtLabel FROM dbo.Orders AS o;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var id = view.FindColumn("Id")!;
        var label = view.FindColumn("CreatedAtLabel")!;

        Assert.False(ColumnProvenanceAnalysis.IsNonDeterministic(id.Provenance));
        Assert.True(ColumnProvenanceAnalysis.IsNonDeterministic(label.Provenance));
    }

    [Fact]
    public void ColumnReferencingOrdinaryComputedColumn_IsNotFlaggedNonDeterministic()
    {
        var lineage = BuildLineage(
            "CREATE TABLE dbo.Orders (Quantity INT NOT NULL, Price INT NOT NULL, Total AS Quantity * Price);",
            "CREATE VIEW dbo.vw_Orders AS SELECT o.Total FROM dbo.Orders AS o;");

        var view = lineage.Find("dbo.vw_Orders")!;
        var total = view.FindColumn("Total")!;

        Assert.False(ColumnProvenanceAnalysis.IsNonDeterministic(total.Provenance));
    }
}
