using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class GotoUsage
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.GotoUsage);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `GOTO` statement anywhere in a routine makes control flow harder to follow - the
            unrestricted-jump maintainability concern any language's GOTO carries. But this finding
            exists for a second, load-bearing reason specific to this tool: this codebase's own dead-
            code analysis (unreachable code, unused labels/variables/parameters, redundant jumps)
            already declines its ENTIRE reachability analysis for the whole routine the moment it
            contains any GOTO at all, since a GOTO can jump control anywhere within the routine and
            makes straightforward reachability analysis unreliable to attempt without much heavier
            machinery.

            Before this finding existed, a GOTO-using routine silently lost that whole other stream's
            coverage with nothing surfacing the reason why - this is the first thing in this codebase
            to actually SURFACE a GOTO's presence as its own reportable fact, rather than only ever
            consuming it internally as a "give up" signal for a different pass. Reported at High
            confidence (an unambiguous structural fact) but as a maintainability risk (SARIF Warning),
            not a provable wrong outcome.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A GOTO inside a procedure",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ValidateAndInsert
                        @Value INT
                    AS
                    BEGIN
                        IF @Value < 0 GOTO InvalidInput;

                        INSERT INTO dbo.T (Value) VALUES (@Value);
                        RETURN;

                        InvalidInput:
                        RAISERROR('Invalid value', 16, 1);
                    END;
                    """,
                NoncompliantExplanation: "The GOTO makes this routine's control flow harder to follow, and it also silently disables this tool's own dead-code/unreachable-code analysis for the whole routine - a real, separate cost beyond the readability concern.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_ValidateAndInsert
                        @Value INT
                    AS
                    BEGIN
                        IF @Value < 0
                        BEGIN
                            RAISERROR('Invalid value', 16, 1);
                            RETURN;
                        END;

                        INSERT INTO dbo.T (Value) VALUES (@Value);
                    END;
                    """,
                CompliantExplanation: "The same validate-then-branch logic expressed with structured IF/RETURN instead of GOTO - no unrestricted jump, and this tool's own dead-code analysis stays fully available for the routine."),
        ]);
}
