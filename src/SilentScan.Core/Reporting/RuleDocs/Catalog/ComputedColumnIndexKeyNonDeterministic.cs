using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class ComputedColumnIndexKeyNonDeterministic
{
    public static string RuleId => SarifRuleCatalog.ComputedColumnIndexKeyRuleId(ComputedColumnIndexKeyFindingKind.NonDeterministic);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An index (or statistics) keys a computed column that is nonpersisted and nondeterministic
            - its expression calls a nondeterministic intrinsic such as NEWID, GETDATE, SYSDATETIME, or
            a no-seed RAND, or references a nondeterministic value such as a global variable.
            Oracle-confirmed (Msg 2729, "Column '...' in table '...' cannot be used in an index or
            statistics or as a partition key because it is non-deterministic"): CREATE INDEX never
            deploys. A PERSISTED computed column is unaffected by this rule - SQL Server already
            refuses to persist a nondeterministic computed column at ALTER/CREATE TABLE time
            (Msg 4936), so by the time an index can reference it, a persisted column is guaranteed
            deterministic.
            """,
        HowToFixIt: """
            Mark the computed column PERSISTED (only possible if its expression is itself
            deterministic), or rewrite the expression to remove the nondeterministic call before
            indexing it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An index keying a computed column that stamps the current time",
                NoncompliantSql: """
                    CREATE TABLE dbo.Notes
                    (
                        NoteId  INT           NOT NULL PRIMARY KEY,
                        Body    VARCHAR(200)  NULL,
                        Tagged  AS (Body + CONVERT(VARCHAR(30), GETDATE()))
                    );

                    CREATE INDEX IX_Notes_Tagged ON dbo.Notes(Tagged);
                    """,
                NoncompliantExplanation: "Tagged is nonpersisted and its expression calls GETDATE(), a nondeterministic intrinsic - this fails with error 2729.",
                CompliantSql: """
                    CREATE TABLE dbo.Notes
                    (
                        NoteId  INT           NOT NULL PRIMARY KEY,
                        Body    VARCHAR(200)  NULL
                    );

                    CREATE INDEX IX_Notes_Body ON dbo.Notes(Body);
                    """,
                CompliantExplanation: "Indexing the underlying deterministic column directly, rather than a nondeterministic derived one, avoids the restriction entirely."),
        ]);
}
