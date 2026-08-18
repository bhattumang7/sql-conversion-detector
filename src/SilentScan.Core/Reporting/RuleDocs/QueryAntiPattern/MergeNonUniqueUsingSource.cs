using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class MergeNonUniqueUsingSource
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.MergeNonUniqueUsingSource);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            MERGE's ON clause pairs each target row with rows from the USING source on the join
            condition, and the WHEN MATCHED branch is defined to act once per matched target row -
            SQL Server does not define what it means to apply a single WHEN MATCHED
            UPDATE/DELETE when a target row's ON-clause key matches more than one row from the
            source. Rather than picking one of the several matching source rows arbitrarily and
            silently, SQL Server raises a hard runtime error - "The MERGE statement attempted to
            UPDATE or DELETE the same row more than once" - the moment it detects, during
            execution, that a target row matched multiple source rows.

            This means MERGE's correctness depends on a property of the USING source that's easy to
            overlook: it must be unique on the columns the ON clause actually joins on, from the
            target's point of view - each target row must be able to match at most one source row.
            If the source is a raw table or view with no uniqueness guarantee on those join
            columns, or a derived query that can return more than one row per join key (a join that
            fans out, an aggregation that isn't actually grouped down to the key, a source table
            that legitimately has more than one row per key for a different reason), the statement
            will run successfully against test data where the key happens to be unique and then
            fail in production the first time real data produces more than one source row for the
            same key - a failure mode that's invisible until data conditions trigger it.

            The error is deliberately unforgiving because MERGE has no defined answer for which of
            several matching source rows should "win" - applying one arbitrarily would silently
            produce a different, unpredictable result depending on plan-dependent physical row
            order, which SQL Server refuses to do.
            """,
        HowToFixIt: """
            Add a unique index or constraint that covers the USING source's own ON-clause join
            columns, so the source is provably unique on the key MERGE joins against and can never
            supply more than one matching row per target row. Where the source is itself a query
            (not a base table) and can't have an index, aggregate or deduplicate it explicitly - by
            the same key the ON clause joins on - before it's used as the MERGE source, so the same
            uniqueness guarantee holds by construction instead of by index.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A MERGE source with no uniqueness guarantee on the join key",
                NoncompliantSql: """
                    CREATE TABLE dbo.Accounts (AccountId INT NOT NULL PRIMARY KEY, Balance DECIMAL(18,2) NOT NULL);
                    CREATE TABLE dbo.PendingAdjustments (AdjustmentId INT NOT NULL PRIMARY KEY, AccountId INT NOT NULL, Delta DECIMAL(18,2) NOT NULL);
                    -- No unique index on PendingAdjustments.AccountId: an account can have several pending adjustments.

                    MERGE dbo.Accounts AS target
                    USING dbo.PendingAdjustments AS source
                        ON target.AccountId = source.AccountId
                    WHEN MATCHED THEN
                        UPDATE SET Balance = target.Balance + source.Delta;
                    """,
                NoncompliantExplanation: "If any AccountId has more than one row in PendingAdjustments, that target row matches multiple source rows on the ON clause - the statement fails at runtime with 'The MERGE statement attempted to UPDATE or DELETE the same row more than once' the moment such a case occurs.",
                CompliantSql: """
                    MERGE dbo.Accounts AS target
                    USING (
                        SELECT AccountId, SUM(Delta) AS Delta
                        FROM dbo.PendingAdjustments
                        GROUP BY AccountId
                    ) AS source
                        ON target.AccountId = source.AccountId
                    WHEN MATCHED THEN
                        UPDATE SET Balance = target.Balance + source.Delta;
                    """,
                CompliantExplanation: "The source is pre-aggregated to one row per AccountId, so it's guaranteed unique on the exact column the ON clause joins on - a target row can never match more than one source row."),
        ]);
}
