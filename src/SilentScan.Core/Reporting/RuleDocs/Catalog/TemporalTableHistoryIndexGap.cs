using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class TemporalTableHistoryIndexGap
{
    public static string RuleId => SarifRuleCatalog.TemporalTableHistoryIndexGapRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A system-versioned temporal table's `FOR SYSTEM_TIME AS OF`/`BETWEEN`/... query rewrites,
            under the hood, to a `UNION ALL` (a Concatenation operator in the plan) of the CURRENT
            table and its HISTORY table - a fact this tool oracle-confirmed directly against a real
            engine (5,000 current-table rows, 2,500 history-table rows, statistics updated with
            `FULLSCAN` on both). When a nonclustered index exists on the current side but no
            structurally matching index exists on the history side, a sargable predicate that
            correctly seeks the current-table branch via its own index still degrades to a full
            Clustered Index Scan on the history-table branch of the same query - the other half of
            the same oracle probe confirmed both branches seek cleanly once a matching index is
            added to the history side.

            "Structurally matching" is an oracle-decided criterion, not an assumed one: the history
            index must carry IDENTICAL key columns in the SAME ORDER (ordinal, case-insensitive) -
            included columns and uniqueness are ignored, since neither affects seek-vs-scan, only
            covering-ness/cost. Key-column order is deliberately treated as significant even though
            a second oracle probe found one case where a reversed key order still produced a seek on
            both branches (a predicate supplying an equality value for every key column) - order-
            sensitivity is the conservative, structurally-safe reading, since a reversed-order
            history index is not guaranteed to rescue the common case of a predicate that only
            supplies the current index's own leading column(s).

            `PRIMARY KEY`/`UNIQUE`-constraint indexes on the current side are never compared against
            the history side at all - not a scope gap, but a structural impossibility this tool
            confirmed directly: SQL Server outright refuses `ALTER TABLE ... ADD CONSTRAINT PRIMARY
            KEY`/`UNIQUE` against a temporal history table, so a valid history table can never carry
            either, by construction. Filtered, columnstore, and disabled indexes are excluded on
            both sides for the same "genuinely seekable" reason this tool applies elsewhere.
            """,
        HowToFixIt: """
            Create a structurally matching nonclustered index (same key columns, same order) on the
            HISTORY table to mirror the one on the CURRENT table.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A current-table index with no matching index on the history table",
                NoncompliantSql: """
                    CREATE TABLE dbo.Widget
                    (
                        Id   INT NOT NULL PRIMARY KEY,
                        Code VARCHAR(20) NOT NULL,
                        ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START,
                        ValidTo   DATETIME2 GENERATED ALWAYS AS ROW END,
                        PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
                    )
                    WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.WidgetHistory));
                    CREATE NONCLUSTERED INDEX IX_Widget_Code ON dbo.Widget (Code);

                    SELECT * FROM dbo.Widget FOR SYSTEM_TIME BETWEEN '2024-01-01' AND '2024-12-31'
                    WHERE Code = 'ABC';
                    """,
                NoncompliantExplanation: "IX_Widget_Code lets the current-table branch of this query seek, but dbo.WidgetHistory carries no matching index on Code - the history-table branch of the same UNION ALL rewrite falls back to a full Clustered Index Scan.",
                CompliantSql: """
                    CREATE NONCLUSTERED INDEX IX_WidgetHistory_Code ON dbo.WidgetHistory (Code);
                    """,
                CompliantExplanation: "With a structurally matching index (same key column, same order) on the history table, both branches of the FOR SYSTEM_TIME rewrite seek instead of one of them scanning."),
        ]);
}
