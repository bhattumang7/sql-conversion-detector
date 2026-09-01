using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.BoundedStringBuiltinTruncation;

internal static class ReplaceResultTruncated
{
    public static string RuleId => SarifRuleCatalog.BoundedStringBuiltinTruncationRuleId(BoundedStringBuiltinTruncationFindingKind.ReplaceResultTruncated);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            REPLACE's return type follows only its first (input) argument, the same way REPLICATE's
            does: a VARCHAR(MAX)/NVARCHAR(MAX) input can grow without limit, but any other input
            type - including a plain string literal, regardless of what the "from"/"to" arguments
            are typed as - caps the result at VARCHAR(8000)/NVARCHAR(4000). This was probed directly
            against a real engine: replacing a shorter substring with a longer one enough times to
            push a non-MAX literal input past that cap comes back silently truncated to exactly the
            cap, with no error.

            When the input, the text being replaced, and its replacement are all compile-time
            constants, the exact resulting length - and how many times the replacement occurs - is
            knowable from the source text alone.
            """,
        HowToFixIt: """
            Cast the input literal to VARCHAR(MAX)/NVARCHAR(MAX) before replacing
            (REPLACE(CAST(@input AS VARCHAR(MAX)), @from, @to)) if the full expanded length is
            genuinely needed - a MAX-typed input is never capped this way.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "REPLACE growing a non-MAX literal past the 8000-byte cap",
                NoncompliantSql: "SELECT REPLACE('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx', 'x', 'yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy');",
                NoncompliantExplanation: "Each of the 100 'x' characters expands to a 90-character replacement, a true length of 9000 bytes; the literal's non-MAX type caps the result at 8000 bytes and the excess is silently dropped.",
                CompliantSql: "SELECT REPLACE(CAST('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' AS VARCHAR(MAX)), 'x', 'yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy');",
                CompliantExplanation: "A VARCHAR(MAX) input is never capped - the full 9000-byte result comes back intact."),
        ]);
}
