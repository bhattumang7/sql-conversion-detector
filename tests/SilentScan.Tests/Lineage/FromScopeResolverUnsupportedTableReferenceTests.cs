using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Lineage;

/// <summary>
/// Covers FromScopeResolver.ResolveUnsupportedTableReference's default arm - the one place in
/// this codebase that ledgers an unhandled ScriptDom node kind generically (by its own
/// GetType().Name) rather than needing a dedicated visitor per construct. Closes a
/// ConstructCoverage.json gap found while auditing "Ledgered" rows for a real verifiedBy
/// reference: the rationale already correctly described this as reachable, real code, but
/// nothing verified it.
/// </summary>
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
        // PIVOT is statically resolvable (every piece - pivoted column names, aggregate
        // function, source/pivot column identifiers - is in the syntax itself, no remote schema
        // needed), unlike OPENQUERY/OPENROWSET just below - no longer routed through the generic
        // unsupported-table-reference fallback.
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

        // SUM(TinyIntCol) widens to Int - oracle-verified, and the identical rule an ordinary
        // SUM(x) in a view's SELECT list already uses.
        var q1 = view.FindColumn("Q1")!;
        Assert.Equal(SqlTypeCategory.Int, Assert.IsType<ColumnProvenance.Expression>(q1.Provenance).InferredType!.Category);

        // Quarter (the pivot column) and Amount (the value column) are consumed by PIVOT, not
        // passed through.
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

        // Amount takes the IN-list's shared TinyInt type unchanged - oracle-verified, no
        // widening (unlike PIVOT's aggregate).
        var amount = view.FindColumn("Amount")!;
        Assert.Equal(SqlTypeCategory.TinyInt, Assert.IsType<ColumnProvenance.Expression>(amount.Provenance).InferredType!.Category);

        // Quarter (the synthesized pivot column) is nvarchar(128) - oracle-verified.
        var quarter = view.FindColumn("Quarter")!;
        var quarterType = Assert.IsType<ColumnProvenance.Expression>(quarter.Provenance).InferredType!;
        Assert.Equal(SqlTypeCategory.NVarChar, quarterType.Category);
        Assert.Equal(128, quarterType.Length);

        // Q1/Q2 (the melted IN-list columns) are consumed by UNPIVOT, not passed through.
        Assert.Null(view.FindColumn("Q1"));
        Assert.Null(view.FindColumn("Q2"));
    }

    [Fact]
    public void Pivot_OverAJoinSource_ResolvesPassthroughColumnsFromBothSides()
    {
        // The gap this fix closes: ResolveTableReference's own switch has no JoinTableReference
        // case at all (join trees are only ever handled by FlattenJoins/AddResolved at the top of
        // ordinary FROM-clause resolution) - calling it directly on a join source fell to the
        // unsupported-table-reference fallback and dropped the WHOLE source, including every
        // passthrough column from both sides of the join.
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
        // Oracle-verified: the engine itself refuses to compile a genuine IN-list type mismatch
        // (Msg 8167) - never a guess about which type would win.
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
