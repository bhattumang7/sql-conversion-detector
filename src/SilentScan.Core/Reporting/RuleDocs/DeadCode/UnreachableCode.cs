using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeadCode;

internal static class UnreachableCode
{
    public static string RuleId => SarifRuleCatalog.DeadCodeRuleId(DeadCodeFindingKind.UnreachableCode);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A statement follows something that always ends the enclosing routine on every reachable
            path - a bare RETURN or THROW, or an IF/TRY-CATCH whose every branch itself always ends
            the routine. Nothing after that point can ever execute, structurally, regardless of what
            data the routine sees at runtime. Most often this is leftover from an edit: a RETURN
            moved earlier during debugging and never removed, or a branch added after logic that
            already exits unconditionally. Either way the dead statement is misleading anyone
            reading the routine into believing it still runs.
            """,
        HowToFixIt: """
            Delete the unreachable statement if it is genuinely obsolete, or fix the preceding
            control flow if reaching it was actually intended (e.g. the earlier RETURN/THROW should
            have been conditional).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A statement after an unconditional RETURN",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.GetStatus AS
                    BEGIN
                        RETURN 1;
                        SELECT 'unreachable';
                    END
                    """,
                NoncompliantExplanation: "RETURN 1 always ends the procedure, so the SELECT below it can never execute on any path.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetStatus AS
                    BEGIN
                        RETURN 1;
                    END
                    """,
                CompliantExplanation: "The dead SELECT is removed, leaving the routine's real behavior unchanged."),
        ]);
}
