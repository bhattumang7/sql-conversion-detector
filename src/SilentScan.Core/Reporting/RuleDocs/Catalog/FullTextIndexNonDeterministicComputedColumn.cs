using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class FullTextIndexNonDeterministicComputedColumn
{
    public static string RuleId => SarifRuleCatalog.FullTextIndexDdlRuleId(FullTextIndexDdlFindingKind.NonDeterministicComputedColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A full-text index names a computed column that is both nonpersisted and nondeterministic
            (its expression calls a nondeterministic intrinsic such as NEWID, GETDATE, SYSDATETIME,
            or a no-seed RAND, or references a nondeterministic value such as a global variable).
            Oracle-confirmed (Msg 9928, "Computed column '...' cannot be used for full-text search
            because it is nondeterministic or imprecise nonpersisted computed column"): the statement
            never deploys. A PERSISTED computed column is unaffected by this rule - SQL Server
            already refuses to persist a nondeterministic computed column at ALTER/CREATE TABLE time
            (Msg 4936), so by the time a full-text index can reference it, a persisted column is
            guaranteed deterministic. Nondeterminism is checked recursively through nested function
            calls (matching the engine's own behavior); a call to a scalar UDF of unknown determinism
            is left unflagged rather than guessed at.
            """,
        HowToFixIt: """
            Mark the computed column PERSISTED (only possible if its expression is itself
            deterministic), or rewrite the expression to remove the nondeterministic call before
            indexing it for full-text search.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A nonpersisted computed column that stamps the current time",
                NoncompliantSql: """
                    CREATE TABLE dbo.Notes
                    (
                        NoteId  INT           NOT NULL PRIMARY KEY,
                        Body    VARCHAR(200)  NULL,
                        Tagged  AS (Body + CONVERT(VARCHAR(30), GETDATE()))
                    );

                    CREATE FULLTEXT INDEX ON dbo.Notes(Tagged)
                        KEY INDEX PK__Notes;
                    """,
                NoncompliantExplanation: "Tagged is nonpersisted and its expression calls GETDATE(), a nondeterministic intrinsic - this fails with error 9928.",
                CompliantSql: """
                    CREATE TABLE dbo.Notes
                    (
                        NoteId  INT           NOT NULL PRIMARY KEY,
                        Body    VARCHAR(200)  NULL
                    );

                    CREATE FULLTEXT INDEX ON dbo.Notes(Body)
                        KEY INDEX PK__Notes;
                    """,
                CompliantExplanation: "Indexing the underlying deterministic column directly, rather than a nondeterministic derived one, avoids the restriction entirely."),
        ]);
}
