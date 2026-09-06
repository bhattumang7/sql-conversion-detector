using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class FullTextIndexNonDeterministicComputedColumn
{
    public static string RuleId => SarifRuleCatalog.FullTextIndexDdlRuleId(FullTextIndexDdlFindingKind.NonDeterministicComputedColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A full-text index names a computed column that is nonpersisted and either nondeterministic
            or imprecise. Nondeterministic: its expression calls a nondeterministic intrinsic such as
            NEWID, GETDATE, SYSDATETIME, or a no-seed RAND, or references a nondeterministic value such
            as a global variable. Imprecise: its expression involves a float/real-typed value anywhere
            in the tree (a float column reference, a float literal, arithmetic over a float operand, or
            an explicit CAST/CONVERT to FLOAT/REAL), or calls a function the engine always treats as
            imprecise regardless of its actual argument types - STR and GREATEST/LEAST are the
            practical ones; the rest of that fixed list (ASIN, ATN2, RADIANS, ATAN, LOG, TAN, SQUARE,
            DEGREES, LOG10, COT, SIN, COS, POWER, EXP, SQRT, ACOS) already resolve to a float return
            type and so are caught by the float-typed-expression check on their own. Oracle-confirmed
            (Msg 9928, "Computed column '...' cannot be used for full-text search because it is
            nondeterministic or imprecise nonpersisted computed column"): the statement never deploys.
            A PERSISTED computed column is unaffected by this rule - SQL Server already refuses to
            persist a nondeterministic computed column at ALTER/CREATE TABLE time (Msg 4936), so by the
            time a full-text index can reference it, a persisted column is guaranteed deterministic;
            imprecision alone does not block persisting, so a persisted imprecise column is still
            excluded. Both checks are applied recursively through nested function calls and
            subexpressions (matching the engine's own per-node behavior); a call to a scalar UDF of
            unknown determinism or return type is left unflagged rather than guessed at.
            """,
        HowToFixIt: """
            Mark the computed column PERSISTED (only possible if its expression is itself
            deterministic), or rewrite the expression to remove the nondeterministic call or the
            float/real-typed value before indexing it for full-text search.
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
            new RuleDocExample(
                Title: "A nonpersisted computed column built from a float expression",
                NoncompliantSql: """
                    CREATE TABLE dbo.Measurements
                    (
                        MeasurementId  INT           NOT NULL PRIMARY KEY,
                        Reading        FLOAT         NOT NULL,
                        ReadingText    AS (CAST(SQRT(Reading) AS NVARCHAR(50)))
                    );

                    CREATE FULLTEXT INDEX ON dbo.Measurements(ReadingText)
                        KEY INDEX PK__Measurements;
                    """,
                NoncompliantExplanation: "ReadingText is nonpersisted and its expression takes SQRT() of a FLOAT column - deterministic, but imprecise, so it still fails with error 9928.",
                CompliantSql: """
                    CREATE TABLE dbo.Measurements
                    (
                        MeasurementId  INT           NOT NULL PRIMARY KEY,
                        Reading        FLOAT         NOT NULL,
                        ReadingText    AS (CAST(SQRT(Reading) AS NVARCHAR(50))) PERSISTED
                    );

                    CREATE FULLTEXT INDEX ON dbo.Measurements(ReadingText)
                        KEY INDEX PK__Measurements;
                    """,
                CompliantExplanation: "Persisting the column sidesteps the imprecision check entirely - only nondeterminism blocks PERSISTED, not imprecision."),
        ]);
}
