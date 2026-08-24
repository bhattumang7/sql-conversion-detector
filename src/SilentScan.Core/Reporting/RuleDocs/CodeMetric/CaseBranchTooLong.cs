using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CodeMetric;

internal static class CaseBranchTooLong
{
    public static string RuleId => SarifRuleCatalog.CodeMetricRuleId(CodeMetricFindingKind.CaseBranchTooLong);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A single CASE WHEN branch's result expression spans more lines than the configured
            maximum. Purely a readability signal - no query result or execution plan is affected. A
            CASE expression's whole point is to pick between short alternatives at a glance; once one
            branch's own expression sprawls across many lines, that at-a-glance property is gone and
            the branch is easy to misread as part of a neighboring one.
            """,
        HowToFixIt: """
            Move the long branch's logic into a named scalar expression, computed column, or helper
            function, and reference that from the CASE branch instead of inlining it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A CASE branch whose result expression sprawls across many lines",
                NoncompliantSql: """
                    SELECT OrderId,
                        CASE
                            WHEN Status = 'Shipped' THEN
                                CONCAT('Shipped on ', FORMAT(ShipDate, 'yyyy-MM-dd'), ' via ',
                                    ISNULL(Carrier, 'unknown carrier'), ', tracking ',
                                    ISNULL(TrackingNumber, 'not yet assigned'))
                            ELSE 'Not shipped'
                        END AS ShippingSummary
                    FROM dbo.Orders;
                    """,
                NoncompliantExplanation: "The 'Shipped' branch's own expression sprawls across several lines, making it easy to misread as belonging to the next branch.",
                CompliantSql: """
                    SELECT o.OrderId,
                        CASE WHEN o.Status = 'Shipped' THEN dbo.FormatShippingSummary(o.ShipDate, o.Carrier, o.TrackingNumber)
                             ELSE 'Not shipped'
                        END AS ShippingSummary
                    FROM dbo.Orders o;
                    """,
                CompliantExplanation: "The long formatting logic moves into a named scalar function; each CASE branch is now a single short line."),
        ]);
}
