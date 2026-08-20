using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class FloatOrRealIndexKeyColumn
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.FloatOrRealIndexKeyColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `float` or `real` column - an approximate, IEEE-754 binary floating-point type - used
            as an index key column is structurally risky regardless of any specific query: an
            approximate type cannot represent every decimal value exactly, and a value computed two
            logically-equivalent-but-differently-rounded ways can compare unequal under `=` even
            though a person would call them "the same number". The index itself still works fine as
            a B-tree - the bytes it stores are exact even though the values they represent are not -
            but any equality seek or comparison against the key inherits the same representation-
            error correctness risk.

            This catalog-only finding flags the structural shape (the column is a key at all),
            independent of whether any scanned query happens to compare on it; a sibling AST-level
            finding flags the sharper, more specific case of an actual equality predicate written
            against a float/real column, wherever one appears in scanned SQL text.
            """,
        HowToFixIt: """
            Avoid float/real columns as index keys, or avoid equality comparisons/seeks against
            them, given IEEE-754 representation error.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A float column used as an index key",
                NoncompliantSql: """
                    CREATE TABLE dbo.Measurements (SensorReading FLOAT NOT NULL, RecordedAt DATETIME2 NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Measurements_Reading ON dbo.Measurements (SensorReading);
                    """,
                NoncompliantExplanation: "The index seeks fine structurally, but any equality comparison against SensorReading (WHERE SensorReading = 98.6) risks silently missing a value that's logically the same number but was computed through a differently-rounded path.",
                CompliantSql: """
                    CREATE TABLE dbo.Measurements (SensorReading DECIMAL(10,4) NOT NULL, RecordedAt DATETIME2 NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Measurements_Reading ON dbo.Measurements (SensorReading);
                    """,
                CompliantExplanation: "DECIMAL is an exact numeric type - two values that represent the same number compare equal reliably, with no IEEE-754 representation-error risk."),
        ]);
}
