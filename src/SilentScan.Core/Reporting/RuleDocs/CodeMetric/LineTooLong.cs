using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CodeMetric;

internal static class LineTooLong
{
    public static string RuleId => SarifRuleCatalog.CodeMetricRuleId(CodeMetricFindingKind.LineTooLong);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A physical source line exceeds the configured maximum character length. This is purely
            a readability signal - no query result or execution plan is affected by line length. A
            very long line usually means several distinct clauses or expressions have been packed
            onto one line, which makes the statement harder to scan, diff, and review.
            """,
        HowToFixIt: """
            Wrap the line at a natural clause boundary (a comma, a boolean operator, a keyword like
            FROM/WHERE/JOIN) so each line stays under the configured maximum.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A single line packing an entire query",
                NoncompliantSql: "SELECT o.OrderId, o.OrderDate, o.CustomerId, c.Name, c.Email FROM dbo.Orders o JOIN dbo.Customers c ON o.CustomerId = c.CustomerId WHERE o.OrderDate >= '2024-01-01';",
                NoncompliantExplanation: "The whole statement is packed onto a single, very long line, making it hard to scan or diff.",
                CompliantSql: """
                    SELECT o.OrderId, o.OrderDate, o.CustomerId, c.Name, c.Email
                    FROM dbo.Orders o
                    JOIN dbo.Customers c ON o.CustomerId = c.CustomerId
                    WHERE o.OrderDate >= '2024-01-01';
                    """,
                CompliantExplanation: "Wrapped at clause boundaries, each line stays well under the configured maximum."),
        ]);
}
