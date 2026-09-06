using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class VectorLiteralNonNumericElement
{
    public static string RuleId => SarifRuleCatalog.VectorLiteralConversionRuleId(SilentScan.Core.Predicates.VectorLiteralConversionFindingKind.NonNumericJsonElement);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server 2025's native `VECTOR(n)` type converts from a string literal by parsing it
            as a JSON array of numbers - via `CAST`/`CONVERT`, a `DECLARE` initializer, or a `SET`
            assignment to a `VECTOR`-typed variable. Confirmed directly against a real SQL Server
            2025 instance: a well-formed JSON array containing a boolean, string, `null`, object,
            or nested array element always fails at execution with Msg 13670 ("Input JSON is not a
            valid Vector"), regardless of what the array's other elements are.

            Only a literal that parses as valid JSON and whose top-level value is a JSON array is
            inspected; a malformed literal or a non-array top-level value is left unflagged rather
            than guessed at, since the engine's own error text diverges in those cases.
            """,
        HowToFixIt: """
            Replace the non-numeric element with a JSON number, or build the vector from an
            expression that only ever produces a numeric JSON array.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A boolean element inside a vector literal always fails",
                NoncompliantSql: """
                    DECLARE @v VECTOR(3) = '[1.0, true, 3.0]';
                    """,
                NoncompliantExplanation: "The JSON array's second element is a boolean - this conversion fails at execution with Msg 13670 every time it runs.",
                CompliantSql: """
                    DECLARE @v VECTOR(3) = '[1.0, 1.0, 3.0]';
                    """,
                CompliantExplanation: "Every element of the JSON array is a number - the conversion succeeds."),
        ]);
}

internal static class VectorLiteralElementCountMismatch
{
    public static string RuleId => SarifRuleCatalog.VectorLiteralConversionRuleId(SilentScan.Core.Predicates.VectorLiteralConversionFindingKind.ElementCountMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A string literal converted to `VECTOR(n)` must supply exactly `n` JSON numbers.
            Confirmed directly against a real SQL Server 2025 instance: a numeric JSON array whose
            element count does not match the target's declared dimension fails at execution with
            Msg 42204 ("The vector dimensions ... and ... do not match") - the same message the
            engine emits when two already-typed `VECTOR` values of different dimensions are
            compared, but here the mismatch is between a literal and its own declared cast target,
            so it is provable from the literal text alone.
            """,
        HowToFixIt: """
            Match the JSON array's element count to the declared `VECTOR(n)` dimension, or change
            `n` to match the literal.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A two-element literal cast to VECTOR(3) always fails",
                NoncompliantSql: """
                    SELECT CAST('[1.0, 2.0]' AS VECTOR(3));
                    """,
                NoncompliantExplanation: "The JSON array has 2 elements but the target declares 3 - this conversion fails at execution with Msg 42204 every time it runs.",
                CompliantSql: """
                    SELECT CAST('[1.0, 2.0, 3.0]' AS VECTOR(3));
                    """,
                CompliantExplanation: "The JSON array's element count matches the declared dimension."),
        ]);
}
