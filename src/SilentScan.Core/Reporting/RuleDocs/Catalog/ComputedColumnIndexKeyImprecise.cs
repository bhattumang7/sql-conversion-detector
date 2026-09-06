using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class ComputedColumnIndexKeyImprecise
{
    public static string RuleId => SarifRuleCatalog.ComputedColumnIndexKeyRuleId(ComputedColumnIndexKeyFindingKind.Imprecise);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An index (or statistics) keys a computed column that is nonpersisted and imprecise - its
            expression involves a float/real-typed value anywhere in the tree (a float column
            reference, a float literal, arithmetic over a float operand, or an explicit CAST/CONVERT to
            FLOAT/REAL), or calls a function the engine always treats as imprecise regardless of its
            actual argument types (STR and GREATEST/LEAST are the practical ones; math functions such
            as SQRT, LOG, or POWER are already caught by the float-typed-expression check on their own,
            since they return FLOAT). Oracle-confirmed (Msg 2799, "Cannot create index or statistics
            '...' on table '...' because the computed column '...' is imprecise and not persisted"):
            CREATE INDEX never deploys. Unlike nondeterminism, imprecision alone does not block
            PERSISTED - SQL Server happily persists a SQRT()-derived column, so this check is decidable
            purely from the column's own expression and the target index's key column list, with no
            dependency on whether the column happens to be persisted for some other reason.
            """,
        HowToFixIt: """
            Mark the computed column PERSISTED, or rewrite the expression to remove the float/real-typed
            value before indexing it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An index keying a computed column built from a float expression",
                NoncompliantSql: """
                    CREATE TABLE dbo.Measurements
                    (
                        MeasurementId  INT           NOT NULL PRIMARY KEY,
                        Reading        FLOAT         NOT NULL,
                        ReadingText    AS (CAST(SQRT(Reading) AS NVARCHAR(50)))
                    );

                    CREATE INDEX IX_Measurements_ReadingText ON dbo.Measurements(ReadingText);
                    """,
                NoncompliantExplanation: "ReadingText is nonpersisted and its expression takes SQRT() of a FLOAT column - deterministic, but imprecise, so it still fails with error 2799.",
                CompliantSql: """
                    CREATE TABLE dbo.Measurements
                    (
                        MeasurementId  INT           NOT NULL PRIMARY KEY,
                        Reading        FLOAT         NOT NULL,
                        ReadingText    AS (CAST(SQRT(Reading) AS NVARCHAR(50))) PERSISTED
                    );

                    CREATE INDEX IX_Measurements_ReadingText ON dbo.Measurements(ReadingText);
                    """,
                CompliantExplanation: "Persisting the column sidesteps the imprecision check entirely - only nondeterminism blocks PERSISTED, not imprecision."),
        ]);
}
