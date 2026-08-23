using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Lineage;

public sealed class FromScopeResolverUnsupportedTableReferenceTests
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
    public void OpenQuery_LedgersUnsupportedTableReference()
    {
        var lineage = BuildLineage(
            "CREATE VIEW dbo.vw_Remote AS SELECT * FROM OPENQUERY(RemoteServer, 'SELECT Id FROM RemoteTable') AS r;");

        Assert.Contains(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "FROM table reference" && e.Reason.Contains("OpenQueryTableReference", StringComparison.Ordinal));
    }

    [Fact]
    public void Pivot_ResolvesPivotedAndPassthroughColumns()
    {
        var lineage = BuildLineage(
            "CREATE TABLE dbo.Sales (OrderId INT NOT NULL, Quarter VARCHAR(2) NOT NULL, Amount TINYINT NOT NULL);",
            """
            CREATE VIEW dbo.vw_SalesPivot AS
            SELECT * FROM (SELECT OrderId, Quarter, Amount FROM dbo.Sales) AS src
            PIVOT (SUM(Amount) FOR Quarter IN ([Q1], [Q2], [Q3], [Q4])) AS p;
            """);

        Assert.DoesNotContain(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "FROM table reference" && e.Reason.Contains("PivotedTableReference", StringComparison.Ordinal));

        var view = lineage.Find("dbo.vw_SalesPivot")!;
        var orderId = view.FindColumn("OrderId")!;
        Assert.Equal(SqlTypeCategory.Int, Assert.IsType<ColumnProvenance.BaseColumn>(orderId.Provenance).Type!.Category);

        var q1 = view.FindColumn("Q1")!;
        Assert.Equal(SqlTypeCategory.Int, Assert.IsType<ColumnProvenance.Expression>(q1.Provenance).InferredType!.Category);

        Assert.Null(view.FindColumn("Quarter"));
        Assert.Null(view.FindColumn("Amount"));
    }

    [Fact]
    public void Unpivot_ResolvesValueAndPivotColumns()
    {
        var lineage = BuildLineage(
            "CREATE TABLE dbo.Wide (Id INT NOT NULL, Q1 TINYINT NOT NULL, Q2 TINYINT NOT NULL);",
            """
            CREATE VIEW dbo.vw_WideUnpivot AS
            SELECT * FROM dbo.Wide UNPIVOT (Amount FOR Quarter IN (Q1, Q2)) AS u;
            """);

        Assert.DoesNotContain(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "FROM table reference" && e.Reason.Contains("UnpivotedTableReference", StringComparison.Ordinal));

        var view = lineage.Find("dbo.vw_WideUnpivot")!;
        var id = view.FindColumn("Id")!;
        Assert.Equal(SqlTypeCategory.Int, Assert.IsType<ColumnProvenance.BaseColumn>(id.Provenance).Type!.Category);

        var amount = view.FindColumn("Amount")!;
        Assert.Equal(SqlTypeCategory.TinyInt, Assert.IsType<ColumnProvenance.Expression>(amount.Provenance).InferredType!.Category);

        var quarter = view.FindColumn("Quarter")!;
        var quarterType = Assert.IsType<ColumnProvenance.Expression>(quarter.Provenance).InferredType!;
        Assert.Equal(SqlTypeCategory.NVarChar, quarterType.Category);
        Assert.Equal(128, quarterType.Length);

        Assert.Null(view.FindColumn("Q1"));
        Assert.Null(view.FindColumn("Q2"));
    }

    [Fact]
    public void Pivot_OverAJoinSource_ResolvesPassthroughColumnsFromBothSides()
    {
        var lineage = BuildLineage(
            "CREATE TABLE dbo.Sales (OrderId INT NOT NULL, CustomerId INT NOT NULL, Quarter VARCHAR(2) NOT NULL, Amount TINYINT NOT NULL);",
            "CREATE TABLE dbo.Customers (CustomerId INT NOT NULL, CustomerName VARCHAR(50) NOT NULL);",
            """
            CREATE VIEW dbo.vw_SalesPivotJoin AS
            SELECT * FROM dbo.Sales s JOIN dbo.Customers c ON c.CustomerId = s.CustomerId
            PIVOT (SUM(Amount) FOR Quarter IN ([Q1], [Q2], [Q3], [Q4])) AS p;
            """);

        var view = lineage.Find("dbo.vw_SalesPivotJoin")!;
        Assert.NotNull(view.FindColumn("OrderId"));
        Assert.NotNull(view.FindColumn("CustomerName"));
        Assert.NotNull(view.FindColumn("Q1"));
    }

    [Fact]
    public void Unpivot_MismatchedInColumnTypes_DeclinesRatherThanGuesses()
    {
        var lineage = BuildLineage(
            "CREATE TABLE dbo.Wide (Id INT NOT NULL, Q1 TINYINT NOT NULL, Q2 INT NOT NULL);",
            """
            CREATE VIEW dbo.vw_WideUnpivotMismatch AS
            SELECT * FROM dbo.Wide UNPIVOT (Amount FOR Quarter IN (Q1, Q2)) AS u;
            """);

        Assert.Contains(
            lineage.Skipped.Entries,
            e => e.ConstructKind == "FROM table reference" && e.Reason.Contains("do not all share one resolved type", StringComparison.Ordinal));

        var view = lineage.Find("dbo.vw_WideUnpivotMismatch")!;
        var amount = view.FindColumn("Amount")!;
        Assert.Null(Assert.IsType<ColumnProvenance.Expression>(amount.Provenance).InferredType);
    }
}
