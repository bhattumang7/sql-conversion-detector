using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class GroupingArgumentNotInGroupByList
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            GROUPING and GROUPING_ID exist to tell rows produced by CUBE, ROLLUP, or GROUPING SETS
            apart from a real NULL - each call's own arguments have to be expressions that already
            appear in the statement's own GROUP BY clause, so the engine can tell which subtotal
            row it is looking at. The binder checks this by matching each argument against the
            GROUP BY clause's own expression list; a column that is not one of them, even if it is
            a perfectly valid column of the same query, fails that match and the statement does not
            compile at all - Msg 8161, "Argument N of the GROUPING function does not match any of
            the expressions in the GROUP BY clause."

            This oracle-confirmed check fires on a plain unmatched column, but also on shapes that
            read as plausible at a glance: a query with no GROUP BY clause at all (nothing can ever
            match), a join where the argument's column shares its name with a GROUP BY column that
            actually belongs to a different table, and GROUPING_ID's later arguments checked
            independently of its earlier ones. A qualification mismatch alone (an unqualified
            column matched against the same column referenced with a table alias in GROUP BY, or
            vice versa) does not trip this - the engine resolves both to the same underlying
            column - so this scan only reports the cases the engine itself rejects.
            """,
        HowToFixIt: """
            Pass GROUPING/GROUPING_ID only the exact column expressions that are already listed in
            the statement's own GROUP BY clause (whether directly, or nested inside its own CUBE,
            ROLLUP, or GROUPING SETS). If the column genuinely needs to be tested for a NULL that
            never comes from grouping, add it to the GROUP BY clause, or use IS NULL against the
            real column instead of GROUPING.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "GROUPING called on a column absent from the GROUP BY clause",
                NoncompliantSql: """
                    SELECT RegionId, GROUPING(SalesYear) AS IsSubtotal, SUM(Amount) AS Total
                    FROM dbo.Sales
                    GROUP BY RegionId;
                    """,
                NoncompliantExplanation: "SalesYear never appears in the GROUP BY clause, so GROUPING(SalesYear) has nothing to match against - the statement does not compile (Msg 8161), regardless of dbo.Sales' own data.",
                CompliantSql: """
                    SELECT RegionId, SalesYear, GROUPING(SalesYear) AS IsSubtotal, SUM(Amount) AS Total
                    FROM dbo.Sales
                    GROUP BY ROLLUP(RegionId, SalesYear);
                    """,
                CompliantExplanation: "SalesYear is now one of the ROLLUP's own grouping expressions, so GROUPING(SalesYear) matches it and the statement compiles."),
        ]);
}
