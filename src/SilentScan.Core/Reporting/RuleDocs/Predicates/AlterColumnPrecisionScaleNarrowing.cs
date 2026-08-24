using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class AlterColumnPrecisionScaleNarrowing
{
    public static string RuleId => SarifRuleCatalog.AlterColumnSafetyRuleId(AlterColumnSafetyKind.PrecisionOrScaleNarrowing);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER TABLE ... ALTER COLUMN can narrow a DECIMAL/NUMERIC column's declared precision
            or scale, or a TIME/DATETIME2/DATETIMEOFFSET column's fractional-seconds scale, below
            what the catalog already records for that column. The engine still has to fit every
            existing row's value into the narrower type, and it does so at DDL time, over the
            actual stored data, not the declared range.

            Two outcomes are both real, and the source text alone cannot tell you which one a
            given deployment will hit: if any stored value's whole-number part no longer fits the
            new precision, the ALTER COLUMN statement itself fails (oracle-confirmed, Msg 8115,
            "Arithmetic overflow error converting numeric to data type numeric"). If every value
            does fit, the statement succeeds silently, and any digits past the new scale are
            rounded away with no warning - the same silent-truncation risk this tool already flags
            for INSERT/UPDATE assignments, but now baked permanently into the column itself. A
            fractional-seconds narrowing on TIME/DATETIME2/DATETIMEOFFSET only ever takes the
            silent-rounding path - narrowing digits past a time value's seconds can't overflow.
            """,
        HowToFixIt: """
            Confirm every existing row's value actually fits the narrower precision/scale before
            narrowing the column, or keep the column at its current precision/scale if any
            existing value would lose digits it needs.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Narrowing DECIMAL(10,4) to DECIMAL(10,2) silently rounds away two digits",
                NoncompliantSql: """
                    CREATE TABLE dbo.Invoice
                    (
                        InvoiceId INT NOT NULL PRIMARY KEY,
                        Total     DECIMAL(10, 4) NOT NULL
                    );

                    ALTER TABLE dbo.Invoice ALTER COLUMN Total DECIMAL(10, 2);
                    """,
                NoncompliantExplanation: "Every existing Total value's fractional part beyond two digits is silently rounded away - the ALTER COLUMN statement itself reports no error.",
                CompliantSql: """
                    CREATE TABLE dbo.Invoice
                    (
                        InvoiceId INT NOT NULL PRIMARY KEY,
                        Total     DECIMAL(10, 4) NOT NULL
                    );
                    """,
                CompliantExplanation: "Leaving the column's scale unchanged keeps every stored digit intact."),
        ]);
}
