using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class ReassignedParameter
{
    public static string RuleId => SarifRuleCatalog.ParameterReassignmentPredicateRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A formal parameter is normally the one case where the optimizer gets a genuinely
            value-specific cardinality estimate for free: parameter sniffing means that when a
            procedure first compiles (or recompiles), the optimizer looks at the actual argument
            value the caller passed for that call and builds the plan's row-count estimates from
            the column-statistics histogram at that specific value, rather than falling back to a
            generic average. This is exactly what a plain DECLARE'd local variable never gets - its
            value is assigned by a separate statement the optimizer doesn't execute, so it's
            invisible at compile time no matter what.

            A parameter loses that advantage the moment it's reassigned - via SET @p = ... or
            SELECT @p = ... - on every statically reachable path before a later predicate uses it.
            The optimizer still sniffs a value for @p, but it's the ORIGINAL value the caller passed
            in, not the new value the reassignment produced and that the predicate will actually run
            against by the time it executes. The sniffed estimate is therefore provably stale: it
            reflects a value the predicate never actually compares against at all. Mechanically this
            sits between the other two predicate-estimate findings - the predicate is not
            using a generic average-density estimate like an untouched local variable would (a value
            genuinely was sniffed), but that sniffed value is guaranteed wrong for this specific
            predicate, which is a distinct and in some ways worse failure than falling back to a
            generic estimate, because it looks and behaves exactly like ordinary, correct parameter
            sniffing until the estimate turns out to be wrong.

            The predicate itself remains fully sargable - the column still appears bare and any
            index on it can still be seeked - only the estimated row count built from the
            now-stale sniffed value is at risk, in the same way it is for a plain local-variable
            predicate. Suppressed when the statement carries OPTION (RECOMPILE) or the procedure is
            WITH RECOMPILE, because both defer estimation to execution time, when the reassigned
            value is actually known.
            """,
        HowToFixIt: """
            Add OPTION (RECOMPILE) to the statement, or WITH RECOMPILE to the procedure, so the
            estimate for the predicate is built from the value @p actually holds at the point the
            predicate runs, not the compile-time-stale value sniffed from the caller's original
            argument. Where the reassignment exists only to apply a default when the caller passes
            NULL, an alternative is restructuring the logic so the predicate compares against the
            parameter's real, final value through a path the optimizer can still sniff correctly -
            but RECOMPILE is the direct fix for the estimate itself.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A parameter reassigned before the predicate that uses it",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT NOT NULL PRIMARY KEY,
                        CustomerId INT NOT NULL
                    );
                    CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);

                    CREATE PROCEDURE dbo.FindOrders (@customerId INT)
                    AS
                    SET @customerId = @customerId + 0;

                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE CustomerId = @customerId;
                    """,
                NoncompliantExplanation: "The optimizer sniffs @customerId's original caller-supplied value to build the estimate, but the predicate actually runs against the value produced by the SET above it - on every call the sniffed estimate reflects a value the predicate never compares against.",
                CompliantSql: """
                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE CustomerId = @customerId
                    OPTION (RECOMPILE);
                    """,
                CompliantExplanation: "OPTION (RECOMPILE) defers estimation until after the SET has run, so the estimate is built from @customerId's actual value at the point the predicate executes."),
        ]);
}
