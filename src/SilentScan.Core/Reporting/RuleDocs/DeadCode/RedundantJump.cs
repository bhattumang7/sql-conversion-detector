using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeadCode;

internal static class RedundantJump
{
    public static string RuleId => SarifRuleCatalog.DeadCodeRuleId(DeadCodeFindingKind.RedundantJump);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A GOTO whose target label is the very next statement in the same straight-line sequence
            jumps to exactly where control flow would already go without it. The GOTO changes
            nothing about execution order - it is pure noise that makes a reader hunt for a jump
            target that turns out to be right where they already were.
            """,
        HowToFixIt: """
            Delete the GOTO - control flow already reaches the same place without it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A GOTO jumping to the very next statement",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.DoWork AS
                    BEGIN
                        GOTO Continue;
                        Continue:
                        SELECT 1;
                    END
                    """,
                NoncompliantExplanation: "Continue is the very next statement after the GOTO, so the jump changes nothing about execution order.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.DoWork AS
                    BEGIN
                        SELECT 1;
                    END
                    """,
                CompliantExplanation: "The no-op GOTO and its now-unreferenced label are both removed; execution order is unchanged."),
        ]);
}
