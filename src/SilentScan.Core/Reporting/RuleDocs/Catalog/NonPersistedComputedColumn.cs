using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class NonPersistedComputedColumn
{
    public static string RuleId => SarifRuleCatalog.NonPersistedComputedColumnRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A computed column with `is_persisted = 0` (the default - a computed column's definition
            is recomputed live on every read, unless `PERSISTED` is explicitly added) recalculates
            its own expression from the base row every single time a query touches it, no matter how
            often the underlying data actually changes. This is a real cost independent of whether
            the column's definition calls a scalar UDF at all: a scalar-UDF-referencing computed
            column already carries its own per-row-call/serial-plan penalty (a separate, already-
            shipped finding), but even a non-persisted computed column built purely from arithmetic
            or string built-ins - `Total AS (Qty * Price)`, say - still pays the per-row recompute
            cost on every read that references it, work that a `PERSISTED` column would have paid
            once, at write time, instead of on every subsequent read.

            This is a pure catalog fact, read directly from `sys.computed_columns.is_persisted` (or
            the column definition's own `PERSISTED` keyword in file mode) - no query-site AST
            walking needed, and no oracle needed either, since "recomputed on every read" is
            definitionally true for a non-persisted computed column, not something that needs
            confirming against a real engine. It never fires on a column whose `PERSISTED` keyword
            is present, regardless of whether that column is also indexed - an indexed, persisted
            computed column has already paid its recompute cost once, at write time.
            """,
        HowToFixIt: """
            Mark the computed column PERSISTED so its definition isn't recomputed from the base row
            on every read that touches it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A non-persisted computed column recomputed on every read",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        Qty   INT NOT NULL,
                        Price MONEY NOT NULL,
                        Total AS (Qty * Price)
                    );
                    """,
                NoncompliantExplanation: "Total has no PERSISTED keyword, so Qty * Price is recalculated from the base row every time a query reads Total - even though Qty and Price only change on write.",
                CompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        Qty   INT NOT NULL,
                        Price MONEY NOT NULL,
                        Total AS (Qty * Price) PERSISTED
                    );
                    """,
                CompliantExplanation: "PERSISTED computes Total once, at write time, and stores the result - every subsequent read gets the stored value instead of recomputing it."),
        ]);
}
