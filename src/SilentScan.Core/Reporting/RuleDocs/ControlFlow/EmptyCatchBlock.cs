using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class EmptyCatchBlock
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.EmptyCatchBlock);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `BEGIN CATCH ... END CATCH` block with zero statements inside it means every error
            that reaches it is silently swallowed - no re-throw, no logging, nothing observable at
            all. The statement that failed simply appears to have succeeded to anything watching the
            outer scope, and whatever real problem triggered the error (a constraint violation, a
            deadlock, a conversion failure) leaves no trace anywhere this tool or a human reviewer
            can find later.

            This is an unambiguous structural fact - a CATCH block with literally no statements in
            its own body - reported at High confidence, the same tier as this codebase's other
            structurally-unambiguous control-flow findings.
            """,
        HowToFixIt: """
            Add at least a THROW or RAISERROR inside the CATCH block, or explicit logging, so a real
            failure doesn't disappear with no trace.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A CATCH block with no statements at all",
                NoncompliantSql: """
                    BEGIN TRY
                        UPDATE dbo.Accounts SET Balance = Balance - 100 WHERE Id = 1;
                    END TRY
                    BEGIN CATCH
                    END CATCH;
                    """,
                NoncompliantExplanation: "If the UPDATE fails for any reason - a constraint violation, a deadlock, anything - the CATCH block swallows it completely with no re-throw and no logging; execution simply continues as if nothing happened.",
                CompliantSql: """
                    BEGIN TRY
                        UPDATE dbo.Accounts SET Balance = Balance - 100 WHERE Id = 1;
                    END TRY
                    BEGIN CATCH
                        THROW;
                    END CATCH;
                    """,
                CompliantExplanation: "THROW re-raises the original error with its original message, severity, and state, so the failure is no longer silently discarded."),
        ]);
}
