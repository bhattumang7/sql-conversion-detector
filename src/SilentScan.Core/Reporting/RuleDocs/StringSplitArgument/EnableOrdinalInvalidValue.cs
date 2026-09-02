using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StringSplitArgument;

internal static class EnableOrdinalInvalidValue
{
    public static string RuleId => SarifRuleCatalog.StringSplitArgumentRuleId(StringSplitArgumentFindingKind.EnableOrdinalInvalidValue);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            STRING_SPLIT's optional third parameter (enable_ordinal) only accepts 0 or 1. This was
            probed directly against a real engine: an integer literal outside that range - e.g. `-1`
            or `3` - raises Msg 4199 ("Argument value ... is invalid for argument 3 of string_split
            function") at bind time, before a single row is read.
            """,
        HowToFixIt: """
            Change the enable_ordinal argument to 0 or 1.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "STRING_SPLIT with an out-of-range enable_ordinal argument",
                NoncompliantSql: "SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 3);",
                NoncompliantExplanation: "3 is neither 0 nor 1 - STRING_SPLIT's enable_ordinal argument raises Msg 4199 for any other integer value.",
                CompliantSql: "SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);",
                CompliantExplanation: "1 is a valid enable_ordinal value."),
        ]);
}
