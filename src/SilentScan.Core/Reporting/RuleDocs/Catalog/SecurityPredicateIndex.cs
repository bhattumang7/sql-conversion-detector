using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class SecurityPredicateIndex
{
    public static string RuleId => SarifRuleCatalog.SecurityPredicateIndexRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An enabled Row-Level Security FILTER predicate is silently applied to EVERY
            `SELECT`/`UPDATE`/`DELETE` against the secured table - not just queries whose own WHERE
            clause happens to filter the same way. When the predicate function's own bound
            (secured-table) column carries no supporting index, the engine has no seek path into the
            table at all through that predicate, and must instead evaluate it as a residual, per-row
            filter over a full scan on every single access - whether or not the calling query's own
            text ever mentions that column.

            Oracle-confirmed directly against a real engine: a genuine `CREATE SECURITY POLICY ...
            ADD FILTER PREDICATE Security.fn_TenantPredicate(TenantId) ON dbo.T WITH (STATE = ON)`
            against a 50,000-row table. With no index on TenantId, a plain `SELECT COUNT(*) FROM
            dbo.T` showed a Clustered Index Scan carrying the inlined predicate function's own logic
            as a residual filter evaluated against every row. With an index on TenantId added and
            the identical query re-run, the plan switched to a genuine Index Seek with no residual
            filter at all - the exact seek-vs-scan contrast this rule exists to catch before it
            happens in production.

            This rule deliberately does NOT claim RLS forces single-threaded execution, even though
            some practitioner guidance does - a real, documented attempt to reproduce that claim
            live (forcing a genuine cost-based, non-trivial plan with parallelism cost threshold
            lowered) showed an RLS-secured query compile with the same degree of parallelism as the
            identical query with the security policy disabled, on this tool's own standing engine
            build. That claim may hold on an earlier engine generation or for a different predicate
            shape, but this tool doesn't assert what it couldn't confirm - only the index-vs-scan
            mechanism, which it did.

            Scoped to only an ENABLED FILTER predicate (a BLOCK predicate doesn't filter the read
            path the same way, and a disabled policy is provably inert), invoked with at least one
            bare column-reference argument this pass can actually resolve. It fires when NONE of the
            predicate's own bound columns individually leads an active index - deliberately
            column-by-column rather than requiring one composite index, since this pass can't see
            the predicate function's own body to know whether multiple bound columns combine with
            AND or OR.
            """,
        HowToFixIt: """
            Add an index (or extend an existing one) covering the Row-Level Security FILTER
            predicate's own bound column(s), so it can be seek-evaluated instead of scanned on every
            access.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An RLS filter predicate bound to a column with no supporting index",
                NoncompliantSql: """
                    CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, TenantId INT NOT NULL);

                    CREATE SECURITY POLICY Security.TenantFilter
                        ADD FILTER PREDICATE Security.fn_TenantPredicate(TenantId) ON dbo.T
                        WITH (STATE = ON);
                    """,
                NoncompliantExplanation: "TenantId carries no index - the RLS predicate is silently applied to every access to dbo.T, and with no index to seek through, the engine evaluates it as a residual filter over a full table scan every time.",
                CompliantSql: """
                    CREATE INDEX IX_T_TenantId ON dbo.T (TenantId);
                    """,
                CompliantExplanation: "With an index on TenantId, the same RLS-secured query plan switches from a Clustered Index Scan with a residual filter to a genuine Index Seek."),
        ]);
}
