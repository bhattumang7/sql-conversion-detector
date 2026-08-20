using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StatementShape;

internal static class BareSelectStar
{
    public static string RuleId => SarifRuleCatalog.StatementShapeRuleId(StatementShapeFindingKind.BareSelectStar);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A bare `SELECT *` anywhere in a query's own outermost projection couples that query to
            the target's current column set - whatever columns the table or view happens to have
            today become the columns this query returns, in whatever order they happen to be
            declared. A later ALTER TABLE adding a column, reordering one, or a view definition
            changing underneath the query silently changes what this query returns too, with no
            error and no signal that anything changed.

            This is the general, any-context version of the concern - distinct from this codebase's
            own narrower, lineage-resolved finding for `SELECT *` specifically inside a view or
            inline TVF that a real downstream consumer then narrows to a strict column subset (a
            sharper, more actionable claim about a specific consumer/view pairing). This rule instead
            fires on any bare SELECT *, anywhere, and is reported at Low confidence specifically
            because a one-off ad-hoc SELECT * (interactive exploration, a quick debugging query) is
            frequently a deliberate, entirely harmless choice - this is a lead worth a second look,
            not a confirmed defect.
            """,
        HowToFixIt: """
            Name the columns explicitly instead of using SELECT *.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A bare SELECT * coupled to the table's current column set",
                NoncompliantSql: """
                    SELECT * FROM dbo.Customers WHERE IsActive = 1;
                    """,
                NoncompliantExplanation: "This query returns whatever columns dbo.Customers happens to have today, in whatever order - a later ALTER TABLE adding or reordering a column silently changes this query's own result shape with no error.",
                CompliantSql: """
                    SELECT CustomerId, Name, Email FROM dbo.Customers WHERE IsActive = 1;
                    """,
                CompliantExplanation: "Naming the columns explicitly fixes this query's result shape independent of whatever columns the table later gains or loses."),
        ]);
}
