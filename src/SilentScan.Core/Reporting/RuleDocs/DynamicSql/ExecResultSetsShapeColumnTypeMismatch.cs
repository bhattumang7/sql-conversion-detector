using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class ExecResultSetsShapeColumnTypeMismatch
{
    public static string RuleId => SarifRuleCatalog.ExecResultSetsShapeColumnTypeMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The sibling half of `exec-with-result-sets-column-count-mismatch`: `EXEC proc WITH
            RESULT SETS ((col ...))` binds by position, and here the column COUNTS match, but at
            least one position's real, engine-described type risks silent data loss against the
            clause's own declared type at that same position - live-verified directly against
            `sys.dm_exec_describe_first_result_set` (compile-only), the same real result-set
            description the count-mismatch sibling uses.

            Unlike the count-mismatch case, this one is genuinely SILENT - the engine converts each
            column's real value into the clause's own declared type at runtime, exactly like a
            normal `INSERT`/`UPDATE` assignment, so the statement simply executes and the data loss
            happens invisibly. This finding reuses the exact same `WriteLossClassifier` machinery
            `insert-exec-temp-table-column-type-mismatch` already applies to the identical
            "assignment across a call boundary" shape - the underlying mechanism (an implicit
            conversion at an assignment point) is the same whether the boundary is a temp table
            INSERT or a WITH RESULT SETS re-declaration, only WHERE in the code the loss happens
            differs.
            """,
        HowToFixIt: """
            Match the WITH RESULT SETS clause's declared column types to the executed procedure's
            real result-set column types at each position.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A result column silently truncated by a narrower WITH RESULT SETS declaration",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_Callee AS
                    BEGIN
                        SELECT CAST('hello world' AS VARCHAR(100)) AS Name;
                    END;

                    CREATE PROCEDURE dbo.usp_Caller AS
                    BEGIN
                        EXEC dbo.usp_Callee WITH RESULT SETS ((Name VARCHAR(3) NOT NULL));
                    END;
                    """,
                NoncompliantExplanation: "dbo.usp_Callee's real Name column is VARCHAR(100), but the WITH RESULT SETS clause declares Name as VARCHAR(3) - column counts match so no error is raised, but every value longer than 3 characters is silently truncated on every call, with nothing about the call site itself looking wrong.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_Caller AS
                    BEGIN
                        EXEC dbo.usp_Callee WITH RESULT SETS ((Name VARCHAR(100) NOT NULL));
                    END;
                    """,
                CompliantExplanation: "The WITH RESULT SETS clause now matches dbo.usp_Callee's own real result-set type exactly, so no character is silently lost."),
        ]);
}
