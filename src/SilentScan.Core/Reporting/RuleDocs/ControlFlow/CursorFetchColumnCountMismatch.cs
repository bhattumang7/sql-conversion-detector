using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class CursorFetchColumnCountMismatch
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `FETCH ... INTO` variable list whose count differs from the statically countable
            column count of its own cursor's defining `SELECT` is not a style complaint - it is a
            real, always-reproducible runtime error, oracle-confirmed directly: SQL Server raises
            Msg 16924, "Cursorfetch: The number of variables declared in the INTO list must match
            that of selected columns," the moment such a `FETCH` executes.

            This rule only fires when the cursor's own defining `SELECT` is a simple, non-`*`,
            non-set-operator query specification whose own column count is directly countable from
            the parse alone - a `SELECT *` or `UNION`-shaped cursor source declines rather than
            guesses, since this pass has no catalog access to resolve `*` into a real column count
            at this point in the pipeline.
            """,
        HowToFixIt: """
            Match the FETCH ... INTO variable list's count to the cursor's own defining SELECT's
            column count exactly.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A FETCH INTO list with fewer variables than the cursor's SELECT columns",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.P AS
                    BEGIN
                        DECLARE @a INT, @b INT;
                        DECLARE cur CURSOR FOR SELECT X, Y, Z FROM dbo.T;
                        OPEN cur;
                        FETCH NEXT FROM cur INTO @a, @b;
                        CLOSE cur;
                        DEALLOCATE cur;
                    END;
                    """,
                NoncompliantExplanation: "The cursor's own SELECT returns 3 columns (X, Y, Z), but the FETCH INTO list only supplies 2 variables (@a, @b) - this raises Msg 16924 the moment FETCH executes, every time, unconditionally.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.P AS
                    BEGIN
                        DECLARE @a INT, @b INT, @c INT;
                        DECLARE cur CURSOR FOR SELECT X, Y, Z FROM dbo.T;
                        OPEN cur;
                        FETCH NEXT FROM cur INTO @a, @b, @c;
                        CLOSE cur;
                        DEALLOCATE cur;
                    END;
                    """,
                CompliantExplanation: "The FETCH INTO list now supplies exactly 3 variables, matching the cursor's own 3-column SELECT."),
        ]);
}
