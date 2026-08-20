using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class DuplicatedCallArgument
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.DuplicatedCallArgument);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The same non-literal expression - a variable, a column reference, or a more complex
            expression - passed as two different arguments to the same `EXEC` or function call is a
            well-documented copy-paste-bug smell: very often one of the two argument positions was
            meant to reference something else, and the repeated reference is a leftover from copying
            the first argument as a starting point for the second.

            A bare literal is deliberately excluded from this check - repeating `NULL`, `0`, or an
            empty string across several optional arguments is completely normal in T-SQL and not
            suspicious at all, so only a repeated variable/column/expression counts. `FORMATMESSAGE`
            is excluded entirely, since deliberately repeating one format-substitution value across
            multiple `%1`/`%2`-style positions is its own normal, intended usage pattern, not a
            mistake.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "The same variable passed as two different EXEC arguments",
                NoncompliantSql: """
                    EXEC dbo.usp_TransferFunds
                        @FromAccountId = @AccountId,
                        @ToAccountId = @AccountId;
                    """,
                NoncompliantExplanation: "@AccountId is passed for BOTH @FromAccountId and @ToAccountId - almost certainly a copy-paste leftover where @ToAccountId was meant to reference a different variable, since a transfer to the same account it came from is a suspicious shape.",
                CompliantSql: """
                    EXEC dbo.usp_TransferFunds
                        @FromAccountId = @SourceAccountId,
                        @ToAccountId = @DestinationAccountId;
                    """,
                CompliantExplanation: "Each argument now references its own distinct variable, matching what a real fund transfer between two different accounts requires."),
        ]);
}
