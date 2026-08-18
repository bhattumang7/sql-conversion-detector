using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class RecursiveCteMissingMaxRecursion
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A recursive common table expression executes its anchor member once and then
            re-executes its recursive member repeatedly, each pass consuming the previous pass's
            output, until a pass produces no new rows. SQL Server caps how many times that
            recursive member is allowed to re-execute, and when no OPTION (MAXRECURSION n) is
            specified on the statement, the cap defaults to exactly 100. This scan's oracle
            confirms the default is enforced, not advisory: exceeding it doesn't return a partial or
            truncated result - the statement is aborted outright with Msg 530, "The maximum
            recursion 100 has been exhausted before statement completion," and nothing from the
            query is returned at all.

            The default of 100 is small enough that it's routinely exceeded by ordinary,
            correctly-written recursive queries the moment the data they walk grows past a shallow
            depth - a bill-of-materials explosion, an org chart walked from the top, a
            date-sequence generator producing more than 100 rows, a hierarchical category tree more
            than 100 levels deep (or, more commonly, more than 100 total rows for a
            counter-style recursive CTE that emits one row per recursion step rather than one row
            per hierarchy level). None of these are runaway or buggy recursion; they're legitimate
            queries that simply need more than 100 iterations to finish, and the default limit has
            nothing to do with whether the recursion is well-formed - it's a fixed safety net
            against infinite recursion from a badly-written termination condition, sized
            conservatively rather than sized to any particular query's real depth.

            Because the failure only manifests once the walked data crosses whatever depth the
            default happens to allow, a recursive CTE with no explicit MAXRECURSION can pass every
            test written against shallow, small fixture data and then fail in production the first
            time the real hierarchy or sequence it's walking grows past 100 levels or 100 rows.
            """,
        HowToFixIt: """
            Add an explicit OPTION (MAXRECURSION n) to the statement, sized to comfortably exceed
            the deepest recursion the query is actually expected to need, rather than leaving it to
            the engine's default. OPTION (MAXRECURSION 0) removes the limit entirely, which is
            appropriate when the recursion's termination is otherwise well-understood and bounded by
            the data itself (a genuinely acyclic hierarchy, a counter with a known upper bound in
            its own termination predicate) rather than by the engine's guard rail.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A recursive CTE walking a hierarchy deeper than the default limit",
                NoncompliantSql: """
                    CREATE TABLE dbo.Categories (CategoryId INT NOT NULL PRIMARY KEY, ParentCategoryId INT NULL, Name VARCHAR(100) NOT NULL);

                    WITH CategoryTree AS
                    (
                        SELECT CategoryId, ParentCategoryId, Name, 0 AS Depth
                        FROM dbo.Categories
                        WHERE ParentCategoryId IS NULL

                        UNION ALL

                        SELECT c.CategoryId, c.ParentCategoryId, c.Name, ct.Depth + 1
                        FROM dbo.Categories AS c
                        JOIN CategoryTree AS ct ON c.ParentCategoryId = ct.CategoryId
                    )
                    SELECT CategoryId, Name, Depth
                    FROM CategoryTree;
                    """,
                NoncompliantExplanation: "With no OPTION (MAXRECURSION ...), the statement is capped at the engine's default of 100 recursive steps - a category tree more than 100 levels deep aborts with Msg 530 and returns nothing at all, even though the recursion itself is correctly formed.",
                CompliantSql: """
                    WITH CategoryTree AS
                    (
                        SELECT CategoryId, ParentCategoryId, Name, 0 AS Depth
                        FROM dbo.Categories
                        WHERE ParentCategoryId IS NULL

                        UNION ALL

                        SELECT c.CategoryId, c.ParentCategoryId, c.Name, ct.Depth + 1
                        FROM dbo.Categories AS c
                        JOIN CategoryTree AS ct ON c.ParentCategoryId = ct.CategoryId
                    )
                    SELECT CategoryId, Name, Depth
                    FROM CategoryTree
                    OPTION (MAXRECURSION 1000);
                    """,
                CompliantExplanation: "An explicit MAXRECURSION sized to the tree's realistic maximum depth means the statement completes instead of aborting once the default's 100-step cap would otherwise have been hit."),
        ]);
}
