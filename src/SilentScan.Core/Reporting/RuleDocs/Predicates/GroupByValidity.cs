using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class GroupByValiditySelectList
{
    public static string RuleId => SarifRuleCatalog.GroupByValidityRuleId(SilentScan.Core.Predicates.GroupByValidityFindingKind.SelectList);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Once a `SELECT` has a plain `GROUP BY` clause, every select-list expression must either be
            an aggregate function call, or shape-identical to one of the `GROUP BY` expressions -
            SQL Server's binder rejects anything else at compile time, before any row is ever touched.

            Oracle-confirmed (Msg 8120, "Column '...' is invalid in the select list because it is not
            contained in either an aggregate function or the GROUP BY clause") - this fails
            unconditionally, decidable purely from the statement's own `GROUP BY` and select-list
            expressions. Unlike some other database engines, SQL Server has no
            functional-dependency-on-primary-key exception: grouping by a table's full primary key
            does not exempt that table's other columns from this restriction.

            Scoped to a plain `GROUP BY` (no `ROLLUP`/`CUBE`/`GROUPING SETS`, whose validity rules
            differ) - within that scope, a `GROUP BY` expression is matched to a select-list
            expression by structural shape (`GROUP BY Id + 1` covers `SELECT Id + 1`, but not
            `SELECT Id + 2`), matching the engine's own binding behavior exactly.
            """,
        HowToFixIt: "Add the column to the GROUP BY clause, or wrap it in an aggregate function.",
        Examples:
        [
            new RuleDocExample(
                Title: "Select-list column outside GROUP BY and outside an aggregate",
                NoncompliantSql: "SELECT Id, Category, SUM(Amount) FROM dbo.Sale GROUP BY Category;",
                NoncompliantExplanation: "Id is neither in the GROUP BY clause nor inside an aggregate function - this statement fails to compile with Msg 8120 every time it runs, even if Id is the table's primary key."),
        ]);
}

internal static class GroupByValidityHaving
{
    public static string RuleId => SarifRuleCatalog.GroupByValidityRuleId(SilentScan.Core.Predicates.GroupByValidityFindingKind.Having);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The identical restriction `GroupByValiditySelectList` documents for the select list also
            applies to the `HAVING` clause: once a `SELECT` has a plain `GROUP BY`, every `HAVING`
            expression must either be an aggregate function call, or shape-identical to one of the
            `GROUP BY` expressions.

            Oracle-confirmed (Msg 8121, "Column '...' is invalid in the HAVING clause because it is
            not contained in either an aggregate function or the GROUP BY clause") - the `HAVING`-
            clause sibling of Msg 8120, failing the same unconditional way.
            """,
        HowToFixIt: "Add the column to the GROUP BY clause, or wrap it in an aggregate function.",
        Examples:
        [
            new RuleDocExample(
                Title: "HAVING column outside GROUP BY and outside an aggregate",
                NoncompliantSql: "SELECT Category, SUM(Amount) FROM dbo.Sale GROUP BY Category HAVING Id > 1;",
                NoncompliantExplanation: "Id is neither in the GROUP BY clause nor inside an aggregate function - this statement fails to compile with Msg 8121 every time it runs."),
        ]);
}

internal static class GroupByValidityOrderBy
{
    public static string RuleId => SarifRuleCatalog.GroupByValidityRuleId(SilentScan.Core.Predicates.GroupByValidityFindingKind.OrderBy);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The identical restriction `GroupByValiditySelectList` documents for the select list also
            applies to the `ORDER BY` clause: once a `SELECT` has a plain `GROUP BY`, every `ORDER BY`
            expression must either be an aggregate function call, or shape-identical to one of the
            `GROUP BY` expressions.

            Oracle-confirmed (Msg 8127, "Column '...' is invalid in the ORDER BY clause because it is
            not contained in either an aggregate function or the GROUP BY clause") - the `ORDER BY`-
            clause sibling of Msg 8120/8121, failing the same unconditional way.
            """,
        HowToFixIt: "Add the column to the GROUP BY clause, or wrap it in an aggregate function.",
        Examples:
        [
            new RuleDocExample(
                Title: "ORDER BY column outside GROUP BY and outside an aggregate",
                NoncompliantSql: "SELECT Category, SUM(Amount) FROM dbo.Sale GROUP BY Category ORDER BY Id;",
                NoncompliantExplanation: "Id is neither in the GROUP BY clause nor inside an aggregate function - this statement fails to compile with Msg 8127 every time it runs."),
        ]);
}
