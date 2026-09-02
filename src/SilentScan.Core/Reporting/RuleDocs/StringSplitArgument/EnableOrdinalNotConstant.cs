using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StringSplitArgument;

internal static class EnableOrdinalNotConstant
{
    public static string RuleId => SarifRuleCatalog.StringSplitArgumentRuleId(StringSplitArgumentFindingKind.EnableOrdinalNotConstant);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            STRING_SPLIT's optional third parameter (enable_ordinal, the switch that adds the ordinal
            output column) only accepts a compile-time constant. This was probed directly against a
            real engine: passing a local variable, a procedure parameter, or a column reference - even
            wrapped in an otherwise-constant expression such as `@flag + 0` - raises Msg 8748 ("The
            enable_ordinal argument for STRING_SPLIT only supports constant values (not variables or
            columns)") at compile/bind time. When the variable is a procedure parameter, the failure
            surfaces at CREATE/ALTER PROCEDURE time itself, before the procedure is ever called.

            An expression built only from literals - e.g. `1 + 0` or `CAST(1 AS BIT)` - is a genuine
            compile-time constant and is accepted; this rule only fires when the expression contains an
            actual variable or column reference somewhere in it.
            """,
        HowToFixIt: """
            Change the enable_ordinal argument to a literal (0 or 1), or a constant expression built
            only from literals.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "STRING_SPLIT with a variable enable_ordinal argument",
                NoncompliantSql: "DECLARE @Flag BIT = 1; SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', @Flag);",
                NoncompliantExplanation: "@Flag is a variable, not a constant - STRING_SPLIT's enable_ordinal argument only accepts constant values, so the call raises Msg 8748.",
                CompliantSql: "SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);",
                CompliantExplanation: "A literal 1 is a valid constant enable_ordinal argument."),
        ]);
}
