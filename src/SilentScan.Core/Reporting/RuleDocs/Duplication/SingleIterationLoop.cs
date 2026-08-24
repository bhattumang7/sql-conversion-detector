using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class SingleIterationLoop
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.SingleIterationLoop);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A WHILE loop's own body unconditionally reaches a BREAK/RETURN/THROW on every path through
            the first iteration - it can never loop a second time. Writing it as a loop misleads a
            reader into thinking it repeats.
            """,
        HowToFixIt: "Replace the loop with plain straight-line code (or an IF), since it can never execute a second iteration.",
        Examples:
        [
            new RuleDocExample(
                Title: "A WHILE loop that always breaks on its first pass",
                NoncompliantSql: """
                    WHILE 1 = 1
                    BEGIN
                        SELECT TOP (1) OrderId FROM dbo.Orders WHERE Status = 'Active';
                        BREAK;
                    END
                    """,
                NoncompliantExplanation: "Every path through the loop body reaches BREAK on the first iteration, so the WHILE can never actually loop.",
                CompliantSql: "SELECT TOP (1) OrderId FROM dbo.Orders WHERE Status = 'Active';",
                CompliantExplanation: "Removing the loop makes clear this always runs exactly once."),
        ]);
}
