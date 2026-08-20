using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicate;

internal static class AggregateDivisionColumnstore
{
    public static string RuleId => SarifRuleCatalog.AggregateDivisionColumnstoreRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `CASE`-guarded division inside an aggregate argument - `SUM(CASE WHEN Denom <> 0 THEN
            Num / Denom ELSE 0 END)`, the idiomatic pattern for avoiding a divide-by-zero error on
            rows where the divisor might be zero - relies on the engine only ever evaluating the
            division on rows where the guard is true. On a table backed by a columnstore index,
            aggregates run under batch-mode, vectorized execution rather than rowstore's per-row
            scalar evaluation, and this is a real, historically-reported class of bug (predominantly
            against earlier columnstore-batch-mode engine generations, SQL Server 2016-2019 era):
            batch-mode CASE/expression evaluation has not always reliably preserved the same
            per-row short-circuit elision rowstore scalar execution guarantees, so a query that has
            run safely under rowstore can start raising a divide-by-zero error once its plan runs in
            batch mode instead.

            <b>This is reported as a structural risk flag only, honestly downgraded after a genuine,
            documented attempt to reproduce the failure live.</b> Real effort was spent trying to
            reproduce the underlying claim directly against this tool's own standing engine build (a
            50,000-row table with a deliberately-seeded zero-divisor subset, a real nonclustered
            columnstore index, a live-confirmed batch-mode plan) across the CASE-guarded form, a
            WHERE-filtered variant, a GROUP BY/hash-aggregate form, swapped THEN/ELSE order, and
            forced parallelism - every variant returned the correct, error-free result on this
            environment's engine build. That does NOT disprove the underlying mechanism (the
            practitioner reports this pattern is drawn from describe a real bug class, just
            predominantly against older engine generations) - it means this tool cannot claim to
            have proven the failure live on the build it actually runs against, unlike every
            oracle-confirmed stream in this codebase. Reported at Low confidence, SARIF Note: a
            real, catalog-decidable structural co-occurrence worth a second look, never a
            proven-wrong-result or even a proven-current-engine-behavior claim.

            Scoped to a definitively provable structural precondition only: the table must carry an
            actual columnstore index (clustered or nonclustered), not merely be eligible for SQL
            Server 2019+'s "Batch Mode on Rowstore" feature - that trigger depends on the optimizer's
            own cost/cardinality estimate for a specific query, workload data this static pass
            cannot see, so it's deliberately excluded rather than over-flagged. A division by a
            literal constant (`Num / 100`) is also excluded - it can never be zero and is not
            error-prone regardless of execution mode.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A CASE-guarded division inside SUM on a columnstore-backed table",
                NoncompliantSql: """
                    CREATE TABLE dbo.Ratios
                    (
                        Id    INT NOT NULL PRIMARY KEY,
                        Num   INT NOT NULL,
                        Denom INT NOT NULL
                    );
                    CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Ratios ON dbo.Ratios (Id, Num, Denom);

                    SELECT SUM(CASE WHEN Denom <> 0 THEN Num / Denom ELSE 0 END) FROM dbo.Ratios;
                    """,
                NoncompliantExplanation: "This CASE guard relies on the engine only evaluating Num / Denom when Denom <> 0 - a documented historical risk pattern in batch-mode execution (which dbo.Ratios's columnstore index triggers) is that this per-row short-circuit isn't always reliably preserved.",
                CompliantSql: """
                    SELECT SUM(Num * 1.0 / NULLIF(Denom, 0)) FROM dbo.Ratios;
                    """,
                CompliantExplanation: "NULLIF(Denom, 0) converts a zero divisor to NULL, which propagates safely through the division and SUM (NULL values are simply skipped by an aggregate) - no CASE guard, and no dependence on short-circuit evaluation order in any execution mode."),
        ]);
}
