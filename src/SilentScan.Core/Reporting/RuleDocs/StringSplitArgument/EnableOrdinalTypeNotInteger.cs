using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StringSplitArgument;

internal static class EnableOrdinalTypeNotInteger
{
    public static string RuleId => SarifRuleCatalog.StringSplitArgumentRuleId(StringSplitArgumentFindingKind.EnableOrdinalTypeNotInteger);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            STRING_SPLIT's optional third parameter (enable_ordinal) only accepts an int/bit-typed
            constant. This was probed directly against a real engine: a string or decimal literal in
            that position - e.g. `'1'` or `1.0` - raises Msg 8116 ("Argument data type ... is invalid
            for argument 3 of string_split function") at compile/bind time, before a single row is
            read.
            """,
        HowToFixIt: """
            Change the enable_ordinal argument to an int or bit literal (0 or 1).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "STRING_SPLIT with a string-typed enable_ordinal argument",
                NoncompliantSql: "SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', '1');",
                NoncompliantExplanation: "'1' is a string literal, not int/bit - STRING_SPLIT's enable_ordinal argument raises Msg 8116 for a non-integer type.",
                CompliantSql: "SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);",
                CompliantExplanation: "An int literal is a valid enable_ordinal argument."),
        ]);
}
