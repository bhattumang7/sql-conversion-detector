using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicate;

internal static class TryCastComputedColumn
{
    public static string RuleId => SarifRuleCatalog.TryCastComputedColumnPredicateRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `TRY_CAST` is session-`DATEFORMAT`-dependent for an ambiguous string-to-date conversion,
            so the engine classifies it non-deterministic - oracle-confirmed directly: `TRY_CAST('03/04/2024'
            AS DATE)` genuinely returned 2024-03-04 under `SET DATEFORMAT mdy` and genuinely returned
            2024-04-03 under `SET DATEFORMAT dmy` - the identical call, the identical input, two
            different results depending purely on session state. A non-persisted computed column
            built on `TRY_CAST` can therefore never be `PERSISTED` at all: this tool confirmed
            directly that `ALTER TABLE ... ADD ParsedDate AS TRY_CAST(RawDate AS DATE) PERSISTED`
            fails at DDL time with the engine's own wording, "Computed column ... cannot be
            persisted because the column is non-deterministic." More importantly for this rule, the
            gap goes further than persistence: even the non-persisted form rejects an ordinary
            `CREATE INDEX` directly against it - "Column ... cannot be used in an index or
            statistics or as a partition key because it is non-deterministic." So a predicate
            filtering on a `TRY_CAST`-based computed column can never seek, no matter what index
            exists elsewhere on the table, because the column itself can never be indexed at all.

            This rule fires only when the computed column's own definition genuinely uses
            `TRY_CAST` AND that same column is referenced inside a real filter context (a WHERE
            clause, a JOIN's own ON clause, or HAVING) somewhere in the scanned code - a
            `TRY_CAST`-based computed column that's never filtered on costs nothing extra beyond the
            per-row recompute cost the sibling `non-persisted-computed-column` rule already reports;
            this rule exists specifically for the "someone is trying to seek through it" case. A
            plain `CAST` (deterministic, indexable) never triggers this rule - only `TRY_CAST`'s own
            session-dependent non-determinism does.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A TRY_CAST computed column filtered in a WHERE clause",
                NoncompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        Id      INT NOT NULL PRIMARY KEY,
                        RawDate VARCHAR(20) NULL,
                        ParsedDate AS TRY_CAST(RawDate AS DATE)
                    );

                    SELECT Id FROM dbo.Events WHERE ParsedDate = '2024-01-01';
                    """,
                NoncompliantExplanation: "ParsedDate can never be indexed - TRY_CAST's non-determinism blocks it from being PERSISTED or from carrying any index at all - so this predicate can never seek, no matter what indexes exist elsewhere on dbo.Events.",
                CompliantSql: """
                    CREATE TABLE dbo.Events
                    (
                        Id      INT NOT NULL PRIMARY KEY,
                        RawDate VARCHAR(20) NULL,
                        ParsedDate AS CAST(RawDate AS DATE) PERSISTED
                    );

                    SELECT Id FROM dbo.Events WHERE ParsedDate = '2024-01-01';
                    """,
                CompliantExplanation: "A plain CAST is deterministic - the computed column can be PERSISTED and indexed, so this predicate can seek. (This trades TRY_CAST's own graceful NULL-on-failure behavior for CAST's hard error on an unparseable RawDate value - a genuine format-validation concern to resolve upstream, not a free substitution in every case.)"),
        ]);
}
