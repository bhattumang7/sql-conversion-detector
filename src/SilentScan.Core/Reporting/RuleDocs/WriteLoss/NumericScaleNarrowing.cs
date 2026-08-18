using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WriteLoss;

internal static class NumericScaleNarrowing
{
    public static string RuleId => SarifRuleCatalog.WriteLossNumericScaleNarrowingRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            DECIMAL/NUMERIC is defined by two numbers: precision (total significant digits) and
            scale (digits to the right of the decimal point). Scale is fixed at the column's own
            definition - a DECIMAL(10,2) column can never hold more than two digits after the
            point, no matter what value is assigned to it. When an INSERT or UPDATE assigns a
            DECIMAL/NUMERIC source value whose own scale is larger than the target column's scale -
            for example a DECIMAL(18,6) computation written into a DECIMAL(10,2) column - SQL
            Server does not reject the assignment or raise a truncation error. It silently rounds
            the value to the target's scale and stores the rounded result.

            This is easy to miss precisely because DECIMAL-to-DECIMAL narrowing looks like it
            "should" be safe - both sides are exact numeric types, unlike the approximate-to-exact
            case, so there's no obvious representational mismatch to raise suspicion. But scale
            narrowing still discards real digits: a computation that intentionally carries extra
            scale through several steps to preserve precision (a currency conversion, a unit price
            times a fractional quantity, an accumulated interest calculation) loses that extra
            precision the instant it's persisted into a narrower column, and every digit past the
            target's scale is gone with no error and no log of what was rounded away.

            The engine's rounding here is a genuine round-half-away-from-zero, not a truncation -
            which means the loss can be small per row, but it compounds across aggregations:
            summing a column of rounded values after the fact does not reproduce what summing the
            unrounded values would have given, and reconciliation against an external system that
            kept the extra scale can drift by fractions of a cent per row, invisible until totals
            are compared line by line.
            """,
        HowToFixIt: """
            Widen the target column's DECIMAL/NUMERIC scale to match (or exceed) the scale of the
            values being written into it, if the extra precision is actually meaningful downstream.
            If the extra digits genuinely don't matter for this column - for example a display
            price that's always meant to be two-decimal currency - round the value explicitly with
            ROUND() to the target's own scale before assigning it, so the rounding is a decision
            visible in the query text rather than an invisible side effect of the column's
            definition.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A six-decimal calculation written into a two-decimal column",
                NoncompliantSql: """
                    CREATE TABLE dbo.InvoiceLines
                    (
                        InvoiceLineId INT           NOT NULL PRIMARY KEY,
                        LineTotal     DECIMAL(10,2) NOT NULL
                    );

                    DECLARE @unitPrice DECIMAL(18,6) = 19.995000;
                    DECLARE @quantity  DECIMAL(18,6) = 3.333333;

                    UPDATE dbo.InvoiceLines
                    SET LineTotal = @unitPrice * @quantity
                    WHERE InvoiceLineId = 1;
                    """,
                NoncompliantExplanation: "@unitPrice * @quantity evaluates at a scale far beyond 2 digits; assigning it into LineTotal DECIMAL(10,2) silently rounds it to two decimal places with no error, discarding the extra precision the calculation carried.",
                CompliantSql: """
                    CREATE TABLE dbo.InvoiceLines
                    (
                        InvoiceLineId INT           NOT NULL PRIMARY KEY,
                        LineTotal     DECIMAL(18,6) NOT NULL
                    );

                    DECLARE @unitPrice DECIMAL(18,6) = 19.995000;
                    DECLARE @quantity  DECIMAL(18,6) = 3.333333;

                    UPDATE dbo.InvoiceLines
                    SET LineTotal = @unitPrice * @quantity
                    WHERE InvoiceLineId = 1;
                    """,
                CompliantExplanation: "LineTotal now carries the same scale as the calculation, so no digits are silently rounded away on write."),
        ]);
}
