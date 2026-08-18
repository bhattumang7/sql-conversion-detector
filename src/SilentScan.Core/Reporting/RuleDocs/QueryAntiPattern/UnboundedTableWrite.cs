using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class UnboundedTableWrite
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.UnboundedTableWrite);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An UPDATE or DELETE statement's WHERE clause is the only mechanism that scopes which
            rows it touches; a TOP clause is the only other mechanism that can bound how many rows
            it touches. A statement carrying neither has no row-limiting mechanism at all - by
            construction, it applies to every single row currently in the table, no matter how many
            that turns out to be.

            The overwhelming majority of UPDATE/DELETE statements written against a specific
            business entity are meant to touch a bounded subset - a customer's orders, rows added
            today, records matching some status - and a missing WHERE clause is very often exactly
            that: a clause that was meant to be there and was dropped, whether by an editing
            mistake, a parameterized WHERE that got stripped along with a debugging step, or a
            copy-paste that lost the filtering condition on the way. When that's what happened, the
            statement doesn't fail or warn - it succeeds, and it succeeds at doing something far
            larger than intended, silently, with no error to signal that anything is different from
            what was meant.

            At the same time, a genuinely unbounded UPDATE/DELETE is sometimes exactly the intended
            operation - a maintenance script clearing a staging table before a reload, an
            end-of-cycle purge of an entire audit log, a deliberate full-table reset. This finding
            can't distinguish those two cases from the statement's shape alone, because they look
            identical: no WHERE, no TOP, every row affected. That's why this is reported as an
            advisory signal rather than a defect - it's flagging "this statement has no
            row-limiting mechanism at all," which is worth a second look regardless of which of the
            two cases it turns out to be.
            """,
        HowToFixIt: """
            If the missing scope was unintentional, add the WHERE clause that should have been
            there. If the whole-table write is genuinely intentional - a deliberate maintenance or
            reset operation - no code change is required, but it's worth treating that intent as
            explicit rather than implicit: a defensive comment stating the write is deliberately
            unbounded, or a guarded confirmation step in whatever deploys the script, turns "looks
            identical to a mistake" into "documented as intentional."
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A DELETE with no WHERE clause and no TOP",
                NoncompliantSql: """
                    CREATE TABLE dbo.OrderLines (OrderLineId INT NOT NULL PRIMARY KEY, OrderId INT NOT NULL, Quantity INT NOT NULL);

                    DELETE FROM dbo.OrderLines;
                    """,
                NoncompliantExplanation: "There is no WHERE clause and no TOP, so every row currently in OrderLines is deleted - correct if this is a deliberate full-table purge, and a serious accidental data loss if a filtering condition was meant to be here and was lost."),
        ]);
}
