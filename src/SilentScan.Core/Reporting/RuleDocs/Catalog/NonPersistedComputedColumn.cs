using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class NonPersistedComputedColumn
{
    public static string RuleId => SarifRuleCatalog.NonPersistedComputedColumnRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A computed column with `is_persisted = 0` (the default - a computed column's definition
            is recomputed live on every read, unless `PERSISTED` is explicitly added) recalculates
            its own expression from the base row every time a query reads it from the base table (or
            from an index that doesn't itself store the column), no matter how often the underlying
            data actually changes. This is a real cost independent of whether the column's definition
            calls a scalar UDF at all: a scalar-UDF-referencing computed column already carries its
            own per-row-call/serial-plan penalty (a separate, already-shipped finding), but even a
            non-persisted computed column built purely from arithmetic or string built-ins -
            `Total AS (Qty * Price)`, say - still pays the per-row recompute cost on every such read,
            work that a `PERSISTED` column would have paid once, at write time, instead of on every
            subsequent read.

            Oracle-confirmed (SQL Server): a nonclustered index whose key or included columns store
            this column's own value lets a read actually served through that index pass the stored
            value straight through - its plan's `Compute Scalar` operator does not re-derive the
            expression from the base row. That only helps reads the optimizer actually serves through
            that specific index; a scan of the base table, or a different index that doesn't store
            the column, still recomputes it per row. The finding calls this out explicitly when a
            covering index exists, rather than claiming an unconditional per-read cost. It never fires
            on a column whose `PERSISTED` keyword is present, regardless of whether that column is
            also indexed - an indexed, persisted computed column has already paid its recompute cost
            once, at write time.
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
