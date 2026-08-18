using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TvfFence;

internal static class NestedUnderViewOrTvf
{
    public static string RuleId => SarifRuleCatalog.TvfFenceNestedUnderViewOrTvfRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A view or inline TVF is transparent to the optimizer - its defining query is expanded
            into the calling query's text before optimization runs, so referencing a view looks,
            cost-wise, exactly like writing its definition out by hand. That transparency is exactly
            what makes it dangerous here: if the view's own definition joins or applies a
            multi-statement table-valued function, the fixed, fabricated cardinality estimate that
            function produces (1 row legacy CE / 100 rows 2014+ CE, with the function body itself
            invisible to the optimizer) is inherited by every query that touches the view, whether
            or not the caller has any idea the view depends on a TVF at all.

            This is the same mechanism as a direct call to a multi-statement TVF or a MSTVF sitting
            in a FROM/JOIN clause, but one layer removed, and that removal is what makes it easy to
            ship: the query that finally trips over the bad estimate reads
            `SELECT ... FROM dbo.vw_CustomerTier`, with no function name anywhere in sight. A
            reviewer checking that query for TVF usage finds nothing to flag. The fence has to be
            traced through the view's own definition - and, if that view itself selects from
            another view, through however many additional layers sit in between - before the actual
            cause of the bad estimate becomes visible.
            """,
        HowToFixIt: """
            Trace the view (or inline TVF) down to whichever underlying object is a multi-statement
            TVF, a `CROSS`/`OUTER APPLY` against one, or a MSTVF in a FROM/JOIN, and apply the same
            fix that would apply if the call were written directly: rewrite the multi-statement TVF
            as a single-statement inline TVF (`RETURN (SELECT ...)`, no `BEGIN...END` block), or
            inline the underlying logic as a derived table/CTE. Once the nested object no longer
            hides an opaque, un-costed body, the view built on top of it inherits accurate
            cardinality estimates the same way it inherited the bad ones.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A view quietly wraps a multi-statement TVF call",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers (CustomerId INT NOT NULL PRIMARY KEY, Name VARCHAR(100) NOT NULL);

                    CREATE FUNCTION dbo.fn_CustomerTier(@CustomerId INT)
                    RETURNS @Tier TABLE (TierName VARCHAR(20))
                    AS
                    BEGIN
                        INSERT INTO @Tier (TierName) SELECT 'Gold';
                        RETURN;
                    END;

                    CREATE VIEW dbo.vw_CustomerTier AS
                    SELECT c.CustomerId, t.TierName
                    FROM dbo.Customers c
                    CROSS APPLY dbo.fn_CustomerTier(c.CustomerId) t;

                    SELECT CustomerId, TierName
                    FROM dbo.vw_CustomerTier;
                    """,
                NoncompliantExplanation: "Nothing in the final query names fn_CustomerTier, but vw_CustomerTier's own definition does - the view's expansion carries the same fixed cardinality estimate and per-row re-execution as calling the function directly.",
                CompliantSql: """
                    CREATE FUNCTION dbo.fn_CustomerTier(@CustomerId INT)
                    RETURNS TABLE
                    AS
                    RETURN (SELECT 'Gold' AS TierName);

                    CREATE VIEW dbo.vw_CustomerTier AS
                    SELECT c.CustomerId, t.TierName
                    FROM dbo.Customers c
                    CROSS APPLY dbo.fn_CustomerTier(c.CustomerId) t;
                    """,
                CompliantExplanation: "Once fn_CustomerTier is an inline TVF, its RETURN query expands into the view's own expansion - the whole chain optimizes as one query with real cardinality estimates, with nothing left opaque for the view to inherit."),
        ]);
}
