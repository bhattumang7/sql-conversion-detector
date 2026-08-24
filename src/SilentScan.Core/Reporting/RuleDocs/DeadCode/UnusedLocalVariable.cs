using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeadCode;

internal static class UnusedLocalVariable
{
    public static string RuleId => SarifRuleCatalog.DeadCodeRuleId(DeadCodeFindingKind.UnusedLocalVariable);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A DECLARE'd local variable that is never read anywhere after being declared - whether it
            is only ever assigned, or never referenced again at all - contributes nothing to the
            routine's result. It is either leftover from a refactor that removed the code using it,
            or a sign that some intended read was never written, silently dropping whatever the
            variable was supposed to carry forward.
            """,
        HowToFixIt: """
            Delete the unused local variable and its assignments, or add the read that was meant to
            use its value.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A variable that is assigned but never read",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.DoWork AS
                    BEGIN
                        DECLARE @count INT = 0;
                        SELECT 1;
                    END
                    """,
                NoncompliantExplanation: "@count is declared and assigned but never read afterward - it has no effect on the procedure's result.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.DoWork AS
                    BEGIN
                        SELECT 1;
                    END
                    """,
                CompliantExplanation: "The dead variable is removed, leaving the routine's real behavior unchanged."),
        ]);
}
