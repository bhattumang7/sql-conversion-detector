using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.View;

internal static class OrderByNotGuaranteedToConsumer
{
    public static string RuleId => SarifRuleCatalog.ViewOrderingRuleId(ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Unlike the sibling `TOP (100) PERCENT` shape, a view or inline TVF's own outermost query
            using a genuinely row-limiting `TOP (N)` (N less than 100, or a non-percent literal) or
            `OFFSET ... FETCH` together with `ORDER BY` is a legitimate use - the ORDER BY really
            does decide which rows survive the limit. What it does NOT do is guarantee the final
            output order of those surviving rows to a consumer that queries the view without its own
            ORDER BY: this is a real, documented Microsoft caveat, and it's easy to misread as
            "working" because the surviving rows often do still appear in the expected order in
            practice - directly confirmed against a real engine, where a view with a genuine `TOP
              (10) ... ORDER BY` was observed to sometimes still appear ordered to a consumer purely
            as a side effect of the chosen plan shape (SQL Server frequently reuses the same sort it
            needed internally to compute the TOP), not because any guarantee exists.

            That's exactly what makes this the more dangerous of the two view-ordering shapes to
            leave unaddressed: it looks correct under whatever query plan happened to run during
            development and testing, then silently stops looking correct the day a statistics
            update, an index change, or a parallel plan picks a different way to compute the same
            TOP - with nothing in the query text changing at all. This finding is reported at Low
            confidence and as a SARIF Note rather than a Warning, since this pass can't see whether
            any real consumer actually depends on the unguaranteed order; it's a documented risk
            flag, not a claim that today's output is wrong.
            """,
        HowToFixIt: """
            Apply an explicit ORDER BY in the consuming query - the view/inline TVF's own ORDER BY
            decides which rows survive TOP/OFFSET-FETCH, but only a consumer's own ORDER BY
            guarantees the final row order it sees.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A view's TOP (N) ORDER BY looks reliable but isn't guaranteed to a consumer",
                NoncompliantSql: """
                    CREATE VIEW dbo.vTopOrders AS
                        SELECT TOP (10) OrderId, Amount
                        FROM dbo.Orders
                        ORDER BY Amount DESC;

                    -- Consumer:
                    SELECT * FROM dbo.vTopOrders;
                    """,
                NoncompliantExplanation: "The view's ORDER BY correctly picks the 10 highest-amount orders, but nothing guarantees the consumer sees them back in descending order - today's plan may happen to preserve it, but a different plan shape (a new index, an updated statistics-driven plan choice) can silently change the order the consumer sees with no change to either query's text.",
                CompliantSql: """
                    SELECT * FROM dbo.vTopOrders ORDER BY Amount DESC;
                    """,
                CompliantExplanation: "The consumer applies its own explicit ORDER BY, which is the only place T-SQL actually guarantees row order to a result set - the view's own internal ORDER BY still correctly selects which 10 rows survive."),
        ]);
}
