using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class DynamicDataMaskingComputedExpressionCollapse
{
    public static string RuleId => SarifRuleCatalog.DynamicDataMaskingRuleId(DynamicDataMaskingFindingKind.ComputedExpressionCollapse);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            For a caller who lacks the `UNMASK` permission, selecting a masked column directly
            returns the masking function's fixed sentinel for that column's declared type - a
            `default()`-masked `DATETIME` shows `1900-01-01`, an `INT` or `FLOAT` shows `0`, and so
            on. Oracle-confirmed against the engine, the same substitution applies to the *entire*
            result of any expression that touches the masked column - `SUM(MaskedAmount)`,
            `DATEADD(day, 1, MaskedDate)`, `CAST(MaskedAmount AS VARCHAR(20))`, and
            `CONCAT('x', MaskedName)` do not compute over the sentinel value and then return that
            computed result; the whole expression collapses to a fresh sentinel for the expression's
            own output type, with no relationship to the real data or to the wrapping computation.

            The result looks like ordinary computed output - a real-looking date, a plausible total -
            but is entirely fabricated by the masking mechanism, not derived from any real value. A
            caller without `UNMASK` who reads a report built this way has no way to tell the
            difference between a genuine computed result and one of these sentinels.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "SUM over a masked column looks like a real total but is a fixed sentinel",
                NoncompliantSql: """
                    CREATE TABLE dbo.Invoices
                    (
                        InvoiceId INT NOT NULL PRIMARY KEY,
                        Amount    DECIMAL(10, 2) MASKED WITH (FUNCTION = 'default()') NOT NULL
                    );

                    SELECT SUM(Amount) AS TotalBilled FROM dbo.Invoices;
                    """,
                NoncompliantExplanation: "For a caller without UNMASK, SUM(Amount) does not add up the default()-masked 0 values for every row - the whole aggregate collapses to a single fixed sentinel for its own result type, which is not a sum of anything and does not grow or shrink as rows change.",
                CompliantSql: """
                    CREATE TABLE dbo.Invoices
                    (
                        InvoiceId INT NOT NULL PRIMARY KEY,
                        Amount    DECIMAL(10, 2) MASKED WITH (FUNCTION = 'default()') NOT NULL
                    );

                    SELECT InvoiceId FROM dbo.Invoices;
                    """,
                CompliantExplanation: "The masked column is not wrapped in any expression, so there is nothing for the caller to mistake for a genuine computed value - Amount itself would still show the plain default() sentinel if selected, which is the documented, expected behavior."),
        ]);
}
