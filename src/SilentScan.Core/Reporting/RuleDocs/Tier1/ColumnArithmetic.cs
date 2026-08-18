using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class ColumnArithmetic
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.ColumnArithmetic);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Arithmetic applied to a column inside a predicate - Quantity * 2 > 100, Price + Tax =
            @total, Balance - 500 < 0 - has exactly the same effect on sargability as any other
            function wrap: the value being compared is the arithmetic expression's result, computed
            fresh per row, not the column's stored value, so an index on the column can't be
            seeked. It reads differently from a CAST or a date-part function because arithmetic
            looks like ordinary data manipulation rather than a type coercion, which is why it's
            easy to write without noticing the cost. A WHERE clause built this way still names the
            real column and still looks selective to the person writing it; the optimizer just has
            no way to use an index built on the column's own values to answer a question about the
            column's values after a transformation.
            """,
        HowToFixIt: """
            Isolate the column on one side of the comparison by moving the arithmetic to the other
            side algebraically. Quantity * 2 > 100 becomes Quantity > 50; Balance - 500 < 0 becomes
            Balance < 500. This works for any predicate where the arithmetic is invertible with a
            constant - which covers the overwhelming majority of real cases, since the other
            operand is usually a literal or a parameter, not another column from the same row.
            When the arithmetic genuinely combines two columns from the same row (Price + Tax =
            @total), there is no column-isolating rewrite available, and a computed, persisted,
            indexed column is the only way to make it sargable, at the cost of maintaining that
            computed column going forward.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Multiplying the column by a constant",
                NoncompliantSql: """
                    CREATE TABLE dbo.OrderLines
                    (
                        OrderLineId INT NOT NULL PRIMARY KEY,
                        Quantity    INT NOT NULL
                    );
                    CREATE INDEX IX_OrderLines_Quantity ON dbo.OrderLines(Quantity);

                    SELECT OrderLineId
                    FROM dbo.OrderLines
                    WHERE Quantity * 2 > 100;
                    """,
                NoncompliantExplanation: "Quantity * 2 is computed per row before the comparison, so IX_OrderLines_Quantity can't be seeked.",
                CompliantSql: """
                    SELECT OrderLineId
                    FROM dbo.OrderLines
                    WHERE Quantity > 50;
                    """,
                CompliantExplanation: "The same condition, rewritten with the constant moved to the other side - Quantity is now bare, and the index seeks directly to the matching range."),
        ]);
}
