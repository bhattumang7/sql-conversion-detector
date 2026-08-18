using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class CatchAllParameter
{
    public static string RuleId => SarifRuleCatalog.CatchAllPredicateRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The shape (Column = @p OR @p IS NULL) is one of the most common idioms in
            hand-written search procedures, because it reads as exactly what the author wants: "if
            the caller supplied a filter value, apply it; otherwise return every row." It's a
            natural way to build one procedure that serves both a filtered and an unfiltered search
            without branching into separate statements. The problem is that SQL Server compiles and
            caches one execution plan for the statement, and that single plan has to remain
            correct no matter which branch actually applies at runtime - the optimizer can't compile
            a different plan for the "@p is NULL, return everything" case versus the "@p has a
            value, filter tightly" case, because it only gets to choose a plan once, at compile
            time, before it knows what any future caller will pass.

            Because one plan must stay correct across both possibilities, the optimizer is forced
            toward a plan that works acceptably (never optimally) for both extremes at once, which
            in practice usually means a full scan regardless of what value is actually supplied on
            a given call - even a call that provides a highly selective value gets the same scan a
            NULL call would need. This is made worse by parameter sniffing: whichever value @p
            happens to hold on the very first call that compiles the plan gets baked in, and every
            subsequent call - regardless of its own @p value - reuses that same cached plan until it's
            evicted or the statement is recompiled.

            This finding is suppressed when the statement carries OPTION (RECOMPILE) or the
            enclosing procedure is created WITH RECOMPILE, because both force the optimizer to
            compile a fresh plan against the real, current value of @p on every single execution -
            at which point it can correctly choose "seek" when a value is supplied and "scan" when
            it isn't, instead of committing to one compromise plan up front.
            """,
        HowToFixIt: """
            Add OPTION (RECOMPILE) to the statement, or WITH RECOMPILE to the procedure, so the
            optimizer builds a plan against the actual runtime value of @p on every call instead of
            one plan that has to stay valid for every possible NULL/non-NULL state at once. This
            trades a small per-execution compilation cost for a plan shaped correctly to each call -
            usually a clear win for a procedure that's called with a real filter value far more
            often than with NULL. If recompiling every call is too costly for a very hot procedure,
            the alternative is splitting the optional-filter logic into separate statements (an IF
            branch per filter combination, or dynamic SQL built per call) so each gets its own
            plan, but that's a larger rewrite than adding RECOMPILE.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An optional-filter search procedure with one cached plan",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT NOT NULL PRIMARY KEY,
                        CustomerId INT NOT NULL
                    );
                    CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);

                    CREATE PROCEDURE dbo.FindOrders (@customerId INT = NULL)
                    AS
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE CustomerId = @customerId OR @customerId IS NULL;
                    """,
                NoncompliantExplanation: "One plan is compiled and cached from whichever @customerId value first triggers compilation, then reused for every later call regardless of whether that call passes a selective CustomerId or NULL - the plan can't adapt per call.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.FindOrders (@customerId INT = NULL)
                    AS
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE CustomerId = @customerId OR @customerId IS NULL
                    OPTION (RECOMPILE);
                    """,
                CompliantExplanation: "OPTION (RECOMPILE) forces a fresh plan on every execution, built against that call's actual @customerId value - a seek when a real value is passed, a scan only when it's genuinely NULL."),
        ]);
}
