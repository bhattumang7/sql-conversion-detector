using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.BoundedStringBuiltinTruncation;

internal static class SpaceResultTruncated
{
    public static string RuleId => SarifRuleCatalog.BoundedStringBuiltinTruncationRuleId(BoundedStringBuiltinTruncationFindingKind.SpaceResultTruncated);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SPACE always returns VARCHAR, and there is no MAX-typed overload or escape hatch the way
            REPLICATE and REPLACE have - the result is unconditionally capped at 8000 bytes, no
            matter how large the requested character count is. This was probed directly against a
            real engine: SPACE(9000) comes back exactly 8000 bytes long (confirmed via DATALENGTH,
            since LEN() itself trims an all-space string's trailing spaces to 0), with no error and
            no warning.

            When the requested count is a compile-time constant, whether it overflows the fixed
            8000-byte cap is knowable from the source text alone.
            """,
        HowToFixIt: """
            Build the padding a different way if more than 8000 spaces are genuinely needed - e.g.
            REPLICATE(CAST(' ' AS VARCHAR(MAX)), @count), which is not capped this way.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "SPACE past the 8000-byte cap",
                NoncompliantSql: "SELECT SPACE(9000);",
                NoncompliantExplanation: "SPACE always returns VARCHAR(8000) at most; the requested 9000 spaces are silently truncated to 8000.",
                CompliantSql: "SELECT REPLICATE(CAST(' ' AS VARCHAR(MAX)), 9000);",
                CompliantExplanation: "A VARCHAR(MAX) source is never capped - the full 9000 spaces come back intact."),
        ]);
}
