using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StatementShape;

internal static class TopWithoutOrderBy
{
    public static string RuleId => SarifRuleCatalog.StatementShapeRuleId(StatementShapeFindingKind.TopWithoutOrderBy);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `TOP` row-limiting clause with no `ORDER BY` anywhere in the same query relies on
            whatever row order the engine happens to produce, and Microsoft's own documentation
            states plainly that which rows come back in this shape is not guaranteed. This isn't an
            inference this pass makes about a specific plan - it's the documented, general contract:
            without an ORDER BY, `TOP (N)` returns SOME N rows, not a defined N rows, and the engine
            is free to change which ones on any later, semantically-identical plan choice (a new
            index, an updated statistics-driven decision, a parallel plan) with the query's own text
            never changing at all.

            The practical risk is exactly the same shape this codebase's view-ordering rules flag
            for a different construct: a query that appears to work correctly today, tested against
            whatever plan happened to run during development, can silently start returning a
            different set of "top" rows the moment the underlying plan shape changes for any reason
            unrelated to this query's own text.
            """,
        HowToFixIt: """
            Add an explicit ORDER BY to the query.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "TOP with no ORDER BY anywhere in the query",
                NoncompliantSql: """
                    SELECT TOP (5) OrderId, Amount FROM dbo.Orders;
                    """,
                NoncompliantExplanation: "With no ORDER BY, which 5 rows come back is not guaranteed by SQL Server's own documented contract - a later plan-shape change can silently return a different 5 rows for the exact same query text.",
                CompliantSql: """
                    SELECT TOP (5) OrderId, Amount FROM dbo.Orders ORDER BY Amount DESC;
                    """,
                CompliantExplanation: "The explicit ORDER BY makes which 5 rows come back a defined, guaranteed outcome - the 5 highest-amount orders, regardless of plan shape."),
        ]);
}
