using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Correctness;

internal static class NonUniqueUpdateSource
{
    public static string RuleId => SarifRuleCatalog.NonUniqueUpdateSourceRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The `UPDATE target SET ... FROM target JOIN source ON ...` form is a T-SQL extension,
            not standard SQL, and its documented contract is narrower than it looks: it's only
            well-defined when each row of `target` matches at most one row of `source`. When a
            target row matches more than one source row - because the source's own join columns
            aren't backed by a unique index or constraint - the ANSI standard has nothing to say
            about which matching source row supplies the value, and SQL Server doesn't pick
            consistently either. It resolves the join to some physical row order determined by the
            chosen execution plan, and updates the target from whichever source row that plan
            happens to visit for that target row. No error, no warning: the statement completes,
            @@ROWCOUNT looks correct, and the values written are simply whichever source row's data
            the plan visited - not necessarily the first, last, or most recent one by any column
            the author cares about.

            The dangerous part is that which source row "wins" is plan-dependent, not
            data-dependent - the exact same statement against the exact same data can pick a
            different source row on a different day, after a statistics update, an index rebuild,
            or a plan cache eviction changes which physical order the join happens to run in. A
            report that looked stable for months can silently start picking different values with
            no code change and no data change, purely because the optimizer chose a different plan
            shape. This is precisely the kind of nondeterminism that's invisible in code review -
            the SET clause reads like ordinary single-value assignment - and invisible in testing
            too, since a small or well-ordered test dataset often happens to produce the same
            result across runs even though the underlying behavior was never actually guaranteed.

            MERGE, by contrast, treats the equivalent situation - a target row matched by more than
            one source row in the WHEN MATCHED clause - as a hard error at execution time
            ("The MERGE statement attempted to UPDATE or DELETE the same row more than once").
            That's a deliberate design difference: MERGE refuses to guess, while UPDATE...FROM
            silently guesses and moves on.
            """,
        HowToFixIt: """
            Add a unique index or unique constraint covering the source's own join columns, so the
            join is guaranteed to match at most one source row per target row and there's no
            ambiguity left for the plan to resolve arbitrarily. If a genuine one-to-many
            relationship is intended and only one source row's values should really apply, make
            that selection explicit - pick it with TOP (1) plus an ORDER BY inside a derived table
            or CTE, or aggregate the source rows down to one value per key - rather than relying on
            whichever row the plan happens to visit. Where the intent really is "update from
            exactly one matching row, and fail loudly if that's not true," MERGE is the safer
            primitive: it raises a hard error instead of picking silently.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "UPDATE...FROM against a source with no unique key on the join column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId INT           NOT NULL PRIMARY KEY,
                        Price     DECIMAL(10,2) NOT NULL
                    );
                    CREATE TABLE dbo.PendingPriceUpdates
                    (
                        UpdateId  INT           NOT NULL PRIMARY KEY,
                        ProductId INT           NOT NULL,
                        NewPrice  DECIMAL(10,2) NOT NULL
                    );
                    -- No unique index/constraint on PendingPriceUpdates.ProductId: the same
                    -- ProductId can legitimately appear on more than one pending row.

                    UPDATE p
                    SET p.Price = u.NewPrice
                    FROM dbo.Products AS p
                    JOIN dbo.PendingPriceUpdates AS u ON u.ProductId = p.ProductId;
                    """,
                NoncompliantExplanation: "If a ProductId has two pending rows with different NewPrice values, SQL Server picks one of them based on plan-dependent physical join order, not on any explicit tiebreak - the same statement can pick a different row on a different execution.",
                CompliantSql: """
                    UPDATE p
                    SET p.Price = latest.NewPrice
                    FROM dbo.Products AS p
                    CROSS APPLY (
                        SELECT TOP (1) u.NewPrice
                        FROM dbo.PendingPriceUpdates AS u
                        WHERE u.ProductId = p.ProductId
                        ORDER BY u.UpdateId DESC
                    ) AS latest;
                    """,
                CompliantExplanation: "The CROSS APPLY explicitly picks the most recent pending update (highest UpdateId) per product, so which row wins is a deliberate, deterministic choice instead of whatever the plan happens to visit first."),
        ]);
}
