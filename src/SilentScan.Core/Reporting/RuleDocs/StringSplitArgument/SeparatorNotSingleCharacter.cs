using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StringSplitArgument;

internal static class SeparatorNotSingleCharacter
{
    public static string RuleId => SarifRuleCatalog.StringSplitArgumentRuleId(StringSplitArgumentFindingKind.SeparatorNotSingleCharacter);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            STRING_SPLIT's separator parameter is declared as nchar(1)/nvarchar(1) - the engine only
            ever accepts exactly one character. This was probed directly against a real engine: a
            separator argument whose length is not exactly one - whether zero characters, two or
            more characters, a literal NULL, or an expression that constant-folds to any of those -
            raises Msg 214 ("Procedure expects parameter 'separator' of type 'nchar(1)/nvarchar(1)'")
            at compile/bind time, before a single row is read. Unlike a runtime data-dependent
            failure, this call never succeeds regardless of what the first argument's own value is.

            This is pure source-level constant-folding with no catalog dependency - a bare string
            literal, a literal NULL, or a constant string concatenation are all resolved the same
            way, without needing to know anything about the surrounding schema.
            """,
        HowToFixIt: """
            Change the separator argument to a single character - STRING_SPLIT accepts exactly one
            character and nothing else.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "STRING_SPLIT with a two-character separator literal",
                NoncompliantSql: "SELECT value FROM STRING_SPLIT('a,,b', ',,');",
                NoncompliantExplanation: "The separator argument ',,' is two characters long - the call raises Msg 214 before any row is read.",
                CompliantSql: "SELECT value FROM STRING_SPLIT('a,,b', ',');",
                CompliantExplanation: "A single-character separator is valid - STRING_SPLIT accepts exactly one character."),
        ]);
}
