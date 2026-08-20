using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StatementShape;

internal static class InsertWithoutColumnList
{
    public static string RuleId => SarifRuleCatalog.StatementShapeRuleId(StatementShapeFindingKind.InsertWithoutColumnList);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An `INSERT` with no explicit column list matches its VALUES/SELECT list against the
            target table's columns purely by ordinal position - the Nth value goes into the Nth
            column, in whatever order the table happens to declare its columns today. That mapping
            is silent and implicit: nothing about the INSERT statement itself records which column
            each value was actually intended for, so the statement's correctness depends entirely on
            the table's column order staying exactly as it was when the INSERT was written.

            The moment that assumption breaks - a new column added in the middle of the table
            rather than at the end, a column reordered by a table rebuild, a column dropped - this
            INSERT either raises a hard error (a column-count mismatch) or, worse, silently succeeds
            while writing values into the wrong columns entirely, with no error at all if the types
            happen to be compatible. An explicit column list makes the intended mapping part of the
            statement's own text, so a later schema change either keeps working correctly or fails
            loudly, never silently.
            """,
        HowToFixIt: """
            Add an explicit column list to the INSERT.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An INSERT relying on the target table's current column order",
                NoncompliantSql: """
                    INSERT INTO dbo.Orders VALUES (1001, 42, '2026-01-15');
                    """,
                NoncompliantExplanation: "Nothing in this statement records which value was meant for which column - if dbo.Orders' column order ever changes, this INSERT either fails with a count mismatch or silently writes values into the wrong columns.",
                CompliantSql: """
                    INSERT INTO dbo.Orders (Id, CustomerId, OrderDate) VALUES (1001, 42, '2026-01-15');
                    """,
                CompliantExplanation: "The explicit column list makes the intended value-to-column mapping part of the statement's own text, independent of the table's current column order."),
        ]);
}
