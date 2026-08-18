using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class CastOrConvertOnColumn
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.CastOrConvertOnColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            CAST/CONVERT applied directly to a column inside a predicate is a specific case of the
            general function-wrapped-column problem: the predicate's real comparison target is the
            converted value, not the column's stored bytes, so an index on the raw column can't be
            seeked. This shows up constantly in code written against a mismatched type - a
            VARCHAR column compared to an INT parameter, or an INT column explicitly CAST to
            VARCHAR to compare against a string literal. Sometimes the CAST is written explicitly
            by the author; just as often it's the engine inserting an IMPLICIT conversion because
            the two sides of a comparison have different types and SQL Server's own data-type
            precedence rules decide which side gets converted (see the ScanForced/RangeSeek
            verdicts for that automatic case). This rule covers the explicit form, where the CAST
            or CONVERT is written directly in the source text around the column.

            Unlike a genuine data-transformation need, this conversion is usually incidental -
            the schema drifted, or a parameter was declared with the wrong type, and the CAST was
            added to make the comparison compile rather than to express anything about the data.
            The fix is almost always to correct the type mismatch at its source instead of paying
            for it on every execution.
            """,
        HowToFixIt: """
            Match the comparison value's type to the column's own declared type, so no conversion
            of the column is needed at all. If the value is a parameter or variable, declare it
            with the column's type directly. If the value is a literal, most literals adapt
            automatically once the parameter/variable does. If the column's own type is genuinely
            wrong for what it stores (an INT column holding values that are really text, for
            example), the durable fix is a schema change - altering the column's type - not a
            CAST at every call site that touches it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An INT column CAST to VARCHAR for a string comparison",
                NoncompliantSql: """
                    CREATE TABLE dbo.Invoices
                    (
                        InvoiceId INT NOT NULL PRIMARY KEY,
                        AccountNo INT NOT NULL
                    );
                    CREATE INDEX IX_Invoices_AccountNo ON dbo.Invoices(AccountNo);

                    SELECT InvoiceId
                    FROM dbo.Invoices
                    WHERE CAST(AccountNo AS VARCHAR(20)) = '4402';
                    """,
                NoncompliantExplanation: "CAST(AccountNo AS VARCHAR(20)) must run per row before the string comparison, so IX_Invoices_AccountNo can never be seeked.",
                CompliantSql: """
                    SELECT InvoiceId
                    FROM dbo.Invoices
                    WHERE AccountNo = 4402;
                    """,
                CompliantExplanation: "Comparing against the INT literal directly needs no conversion of the column at all - the index seeks normally."),
        ]);
}
