using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TvfFence;

internal static class Standalone
{
    public static string RuleId => SarifRuleCatalog.TvfFenceStandaloneRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A multi-statement table-valued function called on its own - as the sole source of a
            query, with nothing else in the FROM clause for its bad estimate to poison and no
            correlated outer row driving repeated re-execution - still carries the fence itself: the
            optimizer can't see the statements inside the function body, so it substitutes a fixed,
            fabricated cardinality estimate (1 row legacy CE / 100 rows 2014+ CE) for whatever the
            function actually returns. Benchmarks comparing multi-statement and inline TVFs over the
            identical result set (same logic, same row count) consistently show the multi-statement
            version costing meaningfully more even in exactly this standalone shape, because the
            fence forces a hidden materialization step - the function's rows are populated into its
            table variable behind an opaque interface before the calling query can read them,
            instead of streaming through as a normal expanded query would.

            This is the mildest of the TVF-fence variants precisely because there's no surrounding
            join or correlated APPLY for the bad estimate to compound against - the finding still
            fires because the fence and the bad estimate are both real, but the practical impact
              scales with how large the function's actual result set is and with what the calling
            code does with it (a downstream sort, aggregate, or join added later inherits the same
            bad estimate this rule would have already flagged).
            """,
        HowToFixIt: """
            Rewrite the function as a single-statement inline TVF (`RETURN (SELECT ...)`, no
            `BEGIN...END` block, no table variable) wherever the logic is expressible as one query.
            An inline TVF's defining query expands into the caller before optimization, exactly like
            a view, so the optimizer derives a real cardinality estimate from the underlying tables'
            statistics instead of substituting a fixed guess - and the hidden materialization step
            goes away because there's no longer a table variable being populated behind an opaque
            interface.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A multi-statement TVF selected from directly",
                NoncompliantSql: """
                    CREATE FUNCTION dbo.fn_ActiveOrderIds()
                    RETURNS @Ids TABLE (OrderId INT)
                    AS
                    BEGIN
                        INSERT INTO @Ids (OrderId) SELECT 1;
                        RETURN;
                    END;

                    SELECT OrderId
                    FROM dbo.fn_ActiveOrderIds();
                    """,
                NoncompliantExplanation: "Even with nothing else in the query, the optimizer still can't see inside fn_ActiveOrderIds' body - it estimates a fixed row count and the function's rows are materialized into @Ids behind an opaque interface before the SELECT can read them.",
                CompliantSql: """
                    CREATE FUNCTION dbo.fn_ActiveOrderIds()
                    RETURNS TABLE
                    AS
                    RETURN (SELECT OrderId FROM dbo.Orders WHERE IsActive = 1);

                    SELECT OrderId
                    FROM dbo.fn_ActiveOrderIds();
                    """,
                CompliantExplanation: "As an inline TVF, the RETURN query expands directly into the calling SELECT - the optimizer estimates its cardinality from Orders' own statistics, and there's no table-variable materialization step in between."),
        ]);
}
