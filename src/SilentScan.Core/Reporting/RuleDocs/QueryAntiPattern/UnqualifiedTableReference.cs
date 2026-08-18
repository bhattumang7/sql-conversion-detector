using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class UnqualifiedTableReference
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.UnqualifiedTableReference);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            When a query names a table with no schema prefix - `SELECT * FROM Orders` rather than
            `SELECT * FROM dbo.Orders` - SQL Server can't resolve which object that name refers to
            purely from the text. Resolution depends on the calling session's default schema (set
            per database user, defaulting to dbo unless configured otherwise) and on which schemas
            in that database actually contain an object named Orders. That resolution happens at
            compile time, and it's session-context-dependent rather than a fixed property of the
            query text alone.

            Because the resolved object can differ by caller context, the plan cache can't safely
            treat two calls of the same unqualified query text as interchangeable just because the
            text matches - it has to key the cached plan on the schema-resolution context as well
            (effectively, the calling user's default schema), so two different users with different
            default schemas issuing what looks like the identical query text get separate cache
            entries and separate compilations rather than sharing one. That's plan-cache bloat and
            duplicated compilation work that a schema-qualified reference avoids entirely, since a
            qualified name resolves to exactly one object regardless of who's asking.

            Compilation itself also pays a direct cost: resolving an unqualified name requires SQL
            Server to take a schema-stability (Sch-S) lock and search the caller's default schema
            (and, historically, sys/dbo fallback paths) to find a matching object, extra work that
            a schema-qualified reference skips because the object is already fully identified by
            the schema name itself. Individually the extra lock and lookup are cheap, but on a
            busy, frequently-compiled/recompiled workload the accumulated extra compilation work
            and plan-cache duplication are a real, measurable cost that's entirely avoidable.
            """,
        HowToFixIt: """
            Qualify every table reference with its schema explicitly - `dbo.Orders` rather than
            `Orders` - so the object is fully identified by the query text itself and resolution
            doesn't depend on the calling session's default schema at all. This also makes the
            query's behavior independent of who runs it: an unqualified reference can silently
            resolve to a different object for a user whose default schema isn't dbo, where a
            qualified reference always resolves to the same one object.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An unqualified table reference at a query site",
                NoncompliantSql: """
                    CREATE SCHEMA sales;
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Amount DECIMAL(10,2) NOT NULL);
                    CREATE TABLE sales.Orders (OrderId INT NOT NULL PRIMARY KEY, Amount DECIMAL(10,2) NOT NULL);

                    SELECT OrderId, Amount
                    FROM Orders
                    WHERE Amount > 100;
                    """,
                NoncompliantExplanation: "Which Orders table this resolves to depends on the calling session's default schema - a user defaulting to dbo gets dbo.Orders, a user defaulting to sales gets sales.Orders, and each distinct resolution context gets its own plan-cache entry for what looks like identical query text.",
                CompliantSql: """
                    SELECT OrderId, Amount
                    FROM dbo.Orders
                    WHERE Amount > 100;
                    """,
                CompliantExplanation: "dbo.Orders is fully identified by the text alone - resolution doesn't depend on the caller's default schema, and every caller shares one plan-cache entry."),
        ]);
}
