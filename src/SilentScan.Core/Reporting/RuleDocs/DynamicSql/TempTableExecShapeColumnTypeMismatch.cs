using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class TempTableExecShapeColumnTypeMismatch
{
    public static string RuleId => SarifRuleCatalog.TempTableExecShapeColumnTypeMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The sibling half of `insert-exec-temp-table-column-count-mismatch`: `INSERT INTO #temp
            EXEC OtherProc` binds by position, and here the column COUNTS match, but at least one
            position's real, engine-described type risks silent data loss against the temp table's
            own declared type at that same position - live-verified directly against
            `sys.dm_exec_describe_first_result_set` (compile-only), the same real result-set
            description the count-mismatch sibling uses.

            Unlike the count-mismatch case, this one is genuinely SILENT - no error, no warning, the
            statement simply executes and the data loss happens invisibly, exactly like this tool's
            other write-loss findings for a normal `INSERT`/`UPDATE` assignment (a narrower target
            type silently truncating or rounding a wider source value). This finding reuses the
            exact same `WriteLossClassifier` machinery `ProcCallArgumentMismatchFinding` already
            applies to the identical "assignment across a call boundary" shape - the underlying
              mechanism (an implicit conversion at an assignment point) is the same whether the
            boundary is a parameter binding or an `INSERT ... EXEC` result-set binding, only WHERE
            in the code the loss happens differs.
            """,
        HowToFixIt: """
            Match #temp's declared column types to the executed procedure's real result-set column
            types at each position.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A Unicode result column silently truncated into a non-Unicode temp column",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_Callee AS
                    BEGIN
                        SELECT CAST(N'x' AS NVARCHAR(100)) AS Name;
                    END;

                    CREATE PROCEDURE dbo.usp_Caller AS
                    BEGIN
                        CREATE TABLE #Results (Name VARCHAR(50) NOT NULL);
                        INSERT INTO #Results EXEC dbo.usp_Callee;
                    END;
                    """,
                NoncompliantExplanation: "dbo.usp_Callee's real Name column is NVARCHAR(100), but #Results declares Name as VARCHAR(50) - column counts match so no error is raised, but any character outside #Results's codepage is silently replaced with '?' on every insert, with nothing about the call site itself looking wrong.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_Caller AS
                    BEGIN
                        CREATE TABLE #Results (Name NVARCHAR(100) NOT NULL);
                        INSERT INTO #Results EXEC dbo.usp_Callee;
                    END;
                    """,
                CompliantExplanation: "#Results's Name column now matches dbo.usp_Callee's own real result-set type exactly, so no character is silently lost at the INSERT ... EXEC boundary."),
        ]);
}
