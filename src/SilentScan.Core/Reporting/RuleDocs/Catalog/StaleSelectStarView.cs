using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class StaleSelectStarView
{
    public static string RuleId => SarifRuleCatalog.StaleSelectStarViewRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A view's own outermost `SELECT *` over a single base table has its column list frozen
            at `CREATE`/`ALTER VIEW`/last `sp_refreshview` time - a later `ALTER TABLE ... ADD/DROP
            COLUMN` on the base table never propagates to the view's own compiled column list. This
            rule catches a view whose frozen column list no longer matches the base table's real,
            current shape - a genuinely different claim from generic "don't SELECT *" style advice
            (deliberately out of scope elsewhere in this codebase): this is specifically about
            metadata drift between a view and its own base table, not query-time cost, and unlike
            the sibling `select-star-view` rule (a frozen list disagreeing with a DIFFERENT
            consuming query's own narrower column selection), this drift already exists between the
            view and its base table with no second consumer site needed at all.

            This is stronger than a milder "a new column is invisible through the view" gap - it's
            oracle-confirmed to produce silently WRONG data under an unchanged column NAME. A real
            probe against a disposable scratch database: `CREATE TABLE Base(Id, A, B)`, `CREATE VIEW
            V AS SELECT * FROM Base` (compiled columns: Id, A, B), then `ALTER TABLE Base ADD C`
            followed by `ALTER TABLE Base DROP COLUMN B` (base table's real current columns: Id, A,
            C). The view's own `sys.columns` row set - and even `sys.dm_exec_describe_first_result_set`'s
            live, describe-only answer - both still report Id, A, B. Actually executing `SELECT *
            FROM V` with a real row (A = 1, the new column C = 99) returned a row labeled Id, A, B
            whose third value was 99 - the live data physically occupying the third column slot
            (now really C) surfaced under the view's stale, frozen label B. A consumer reading this
            view's "B" column today is silently reading real "C" data.

            Deliberately scoped to v1: only the view's own outermost query specification's bare or
            qualified `*`, selecting from exactly one real base table (no join, no derived table, no
            CTE), is inspected - a join or a nested-subquery star is a known v1 scope limit, not
            silently missed. A CTE sharing the base table's own name is correctly declined rather
            than misattributed - a CTE is never schema-qualified, so it always shadows a same-named
            real base table for the view's own lifetime.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A dropped-then-added column shifts identity under an unchanged view label",
                NoncompliantSql: """
                    CREATE TABLE dbo.Base (Id INT, A INT, B INT);
                    CREATE VIEW dbo.V AS SELECT * FROM dbo.Base;
                    -- View's own compiled columns, frozen at CREATE VIEW time: Id, A, B

                    ALTER TABLE dbo.Base ADD C INT;
                    ALTER TABLE dbo.Base DROP COLUMN B;
                    -- Base table's real current columns: Id, A, C
                    """,
                NoncompliantExplanation: "dbo.V's compiled column list is still Id, A, B - the view never re-expands its own frozen star. A consumer querying dbo.V's \"B\" column is actually reading the real column C's data, silently mislabeled.",
                CompliantSql: """
                    EXEC sp_refreshview 'dbo.V';
                    """,
                CompliantExplanation: "sp_refreshview recompiles the view's column list against the base table's real current shape, so the view's own columns (Id, A, C) match what the base table actually has."),
        ]);
}
