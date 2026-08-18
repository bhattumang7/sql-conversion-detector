using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ScalarUdf;

internal static class NestedUnderViewOrTvf
{
    public static string RuleId => SarifRuleCatalog.ScalarUdfNestedUnderViewOrTvfRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A view or inline TVF is transparent to the optimizer - referencing one expands its
            defining query into the caller before optimization, exactly as if the caller had written
            that query out by hand. That means if the view's own SELECT list, or an inline TVF it in
            turn depends on, calls a scalar UDF anywhere in the chain, the per-row execution cost -
            and, when the function isn't provably inlineable, the forced-serial plan - is inherited
            by every query that touches the view, regardless of how many layers of views sit between
            the final caller and the function call.

            This is easy to miss for the same reason the TVF-fence nesting case is: the query that
            finally pays the cost reads `SELECT ... FROM dbo.vw_LineItemPricing WHERE
            DiscountedPrice > 100.00`, with no function name anywhere in sight and nothing in the
            WHERE clause that looks unusual. The scalar UDF call is sitting inside the view's own
            SELECT list, computing DiscountedPrice as a derived column - so from the caller's side
            this looks like an ordinary column comparison, and only tracing the view's definition (and
            recursively, anything it in turn selects from) reveals the per-row function call actually
            driving the cost.
            """,
        HowToFixIt: """
            Trace the view or inline TVF down to wherever it calls the scalar UDF, and apply the
            same fix that would apply to a direct call: if the function body is a single expression
            that qualifies for SQL Server 2019+ inlining, upgrading the target engine may resolve
            this without any rewrite. Otherwise, inline the function's expression directly into the
            view's own SELECT list in place of the function call, so nothing in the chain requires a
            separate per-row invocation.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A view computes a column via a scalar UDF, invisibly to the caller",
                NoncompliantSql: """
                    CREATE FUNCTION dbo.discount_price(@price DECIMAL(12,2), @discount DECIMAL(12,2))
                    RETURNS DECIMAL(12,2)
                    AS
                    BEGIN
                        RETURN @price * (1 - @discount);
                    END;

                    CREATE TABLE dbo.LineItem
                    (
                        LineItemId    INT           NOT NULL PRIMARY KEY,
                        ExtendedPrice DECIMAL(12,2) NOT NULL,
                        Discount      DECIMAL(12,2) NOT NULL
                    );

                    CREATE VIEW dbo.vw_LineItemPricing AS
                    SELECT LineItemId, dbo.discount_price(ExtendedPrice, Discount) AS DiscountedPrice
                    FROM dbo.LineItem;

                    SELECT LineItemId
                    FROM dbo.vw_LineItemPricing
                    WHERE DiscountedPrice > 100.00;
                    """,
                NoncompliantExplanation: "Nothing in the final query names discount_price, but vw_LineItemPricing's SELECT list does - the view's expansion carries the same per-row invocation cost (and, if not inlined, forced-serial plan) as calling the function directly in the WHERE clause.",
                CompliantSql: """
                    CREATE VIEW dbo.vw_LineItemPricing AS
                    SELECT LineItemId, ExtendedPrice * (1 - Discount) AS DiscountedPrice
                    FROM dbo.LineItem;
                    """,
                CompliantExplanation: "The expression is inlined directly into the view's SELECT list - no function call anywhere in the chain, so every caller of the view gets an ordinary computed expression instead of a per-row routine invocation."),
        ]);
}
