using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StatementShape;

internal static class OrdinalOrderBy
{
    public static string RuleId => SarifRuleCatalog.StatementShapeRuleId(StatementShapeFindingKind.OrdinalOrderBy);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An `ORDER BY` referencing a SELECT-list position by ordinal number (`ORDER BY 2`)
            instead of a column name ties the sort order to the SELECT list's own column
            position rather than to any named column. That binding is silent: nothing about the
            ORDER BY clause itself names which column it actually means to sort by, only which
            position in the list happens to hold it today.

            The moment the SELECT list's own column order changes - a column inserted earlier in the
            list, columns reordered during a routine edit, a column removed - the ordinal ORDER BY
            keeps referring to whatever new column now sits at that position, silently sorting by a
            completely different column with no error raised at all. Referencing the column by name
            instead makes the sort's real intent part of the statement's own text, immune to later
            reordering of the SELECT list.
            """,
        HowToFixIt: """
            Reference the ORDER BY column by name instead of its SELECT-list ordinal position.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An ORDER BY referencing a SELECT-list position by ordinal",
                NoncompliantSql: """
                    SELECT CustomerName, OrderDate FROM dbo.Orders ORDER BY 2;
                    """,
                NoncompliantExplanation: "ORDER BY 2 means \"whatever is in the second SELECT-list position\" - today that's OrderDate, but if a column is later inserted before it in the SELECT list, this silently starts sorting by that new column instead, with no error.",
                CompliantSql: """
                    SELECT CustomerName, OrderDate FROM dbo.Orders ORDER BY OrderDate;
                    """,
                CompliantExplanation: "Naming OrderDate directly ties the sort to that specific column, immune to any later reordering of the SELECT list."),
        ]);
}
