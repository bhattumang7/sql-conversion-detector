using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Query;

internal static class BareTopNoOrderBy
{
    public static string RuleId => SarifRuleCatalog.BareTopNoOrderByRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            TOP (n) without an ORDER BY doesn't mean "the first n rows" in any meaningful sense -
            SQL Server's own documentation is explicit that the rows a TOP clause returns, and the
            order those rows are returned in, are both undefined when there's no ORDER BY governing
            the query. TOP simply stops the query after it has produced n rows from whatever
            order the chosen execution plan happens to produce them in, and that order is an
            artifact of the plan - which index the optimizer chose to scan, whether it read forward
            or backward through that index, whether the query went parallel and which worker
            thread's rows got interleaved first. None of those are things the query text specifies
            or the engine promises to keep stable.

            This is easy to miss because a given TOP query, run against a given database on a given
            day, usually does look stable - the same plan tends to get chosen repeatedly, and a
            small table scanned via its clustered index in physical row order often does happen to
            return rows in something close to insertion order. That apparent stability is
            coincidental, not guaranteed, and it breaks precisely when something about the
            environment changes rather than something about the code: a statistics update or index
            rebuild that flips the optimizer's chosen access path, a plan that goes parallel once
            the table crosses a row-count threshold it didn't used to cross, a forced index change,
            or simply moving the same query to a different server or a restored copy of the
            database. Code that silently depended on "TOP always gives me the most recent N rows"
            or "the same N rows every time" can start returning a different N rows with no error,
            no schema change, and no code change at all - exactly the kind of environment-dependent
            behavior that's nearly impossible to reproduce once reported, because it isn't tied to
            anything in source control.

            TOP (100) PERCENT is deliberately excluded from this rule: 100 percent of the result set
            is every row the query would otherwise produce regardless of what order TOP itself
            picks them in, so there's no row-selection nondeterminism left for a missing ORDER BY
            to expose - TOP's own selection behavior is moot when the percentage is the whole set.
            """,
        HowToFixIt: """
            Add an explicit ORDER BY to the query that TOP applies to, specifying exactly which
            column(s) determine "the first N" - most recent by a timestamp column, highest by a
            score column, whatever the query's actual intent is. This makes both which rows TOP
            returns and their order a guarantee of the query itself, not an accident of whichever
            plan the optimizer happens to choose on a given execution.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "TOP with no ORDER BY assumed to mean \"most recent\"",
                NoncompliantSql: """
                    CREATE TABLE dbo.AuditLog
                    (
                        AuditId   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        EventTime DATETIME2(3)       NOT NULL,
                        Message   VARCHAR(200)        NOT NULL
                    );

                    SELECT TOP (10) AuditId, EventTime, Message
                    FROM dbo.AuditLog;
                    """,
                NoncompliantExplanation: "With no ORDER BY, SQL Server is free to return any 10 rows in any order - the code likely intends \"the 10 most recent events,\" but nothing in the query guarantees that, and the actual rows returned can change if the optimizer picks a different plan.",
                CompliantSql: """
                    SELECT TOP (10) AuditId, EventTime, Message
                    FROM dbo.AuditLog
                    ORDER BY EventTime DESC;
                    """,
                CompliantExplanation: "ORDER BY EventTime DESC makes \"most recent 10\" an explicit, guaranteed contract of the query instead of an accident of plan choice."),
        ]);
}
