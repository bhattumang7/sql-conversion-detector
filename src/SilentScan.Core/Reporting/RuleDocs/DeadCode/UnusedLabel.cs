using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeadCode;

internal static class UnusedLabel
{
    public static string RuleId => SarifRuleCatalog.DeadCodeRuleId(DeadCodeFindingKind.UnusedLabel);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A label that no GOTO in the same routine ever targets does nothing at runtime - T-SQL
            labels are pure jump targets with no other effect. Its presence usually means a GOTO
            that used to reference it was deleted or renamed, or the label was added for a jump that
            was never written. Either way it is dead weight that invites a reader to assume some
            jump reaches it when none does.
            """,
        HowToFixIt: """
            Delete the unused label, or add the GOTO that was meant to jump to it if control flow
            was actually intended to reach it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A label with no GOTO targeting it",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.DoWork AS
                    BEGIN
                        SELECT 1;
                        Cleanup:
                        SELECT 2;
                    END
                    """,
                NoncompliantExplanation: "No GOTO Cleanup exists anywhere in the routine, so the label is never a jump target - it just sits there.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.DoWork AS
                    BEGIN
                        SELECT 1;
                        SELECT 2;
                    END
                    """,
                CompliantExplanation: "The unreferenced label is removed; control flow is identical since nothing ever jumped to it."),
        ]);
}
