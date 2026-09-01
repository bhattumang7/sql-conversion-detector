using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class TempTableExecShapeColumnCountMismatch
{
    public static string RuleId => SarifRuleCatalog.TempTableExecShapeColumnCountMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `INSERT INTO #temp EXEC OtherProc` binds the executed procedure's result set into the
            temp table's own columns purely by POSITION - never by name. That's an implicit
            assumption every such statement makes: that `OtherProc`'s actual, engine-described
            result set matches the INSERT's own target column list one-for-one in order - `#temp`'s
            full declared columns, or a narrower explicit `INSERT INTO #temp (col, ...) EXEC` column
            list when the statement names one (any declared column left out of that list, DEFAULT
            or nullable, is simply not touched and needs no matching described column at all). When
            the executed procedure's real column COUNT differs from that target column count,
            T-SQL raises a hard, immediate runtime error (Msg 213 or 8164, "column name or number of
            supplied values does not match table definition") the instant the statement executes -
            live-verified directly against `sys.dm_exec_describe_first_result_set` (compile-only),
            the same real, engine-authoritative result-set description this tool's other
            live-catalog facts already rely on.

            Unlike the sibling `insert-exec-temp-table-column-type-mismatch` finding, this is not
            itself a SILENT defect - the engine fails loudly, every time. It's still worth reporting,
            because it names a query this tool can PROVE fails at runtime on every single execution,
            a stronger and more actionable claim than static analysis can normally make about
            runtime behavior at all.
            """,
        HowToFixIt: """
            Make the INSERT's own target column count - #temp's declared column list, or its own
            explicit column list - match the executed procedure's real result-set column count
            exactly. INSERT ... EXEC binds purely by position.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A temp table declared with more columns than the executed procedure returns",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_Callee AS
                    BEGIN
                        SELECT 1 AS Id;
                    END;

                    CREATE PROCEDURE dbo.usp_Caller AS
                    BEGIN
                        CREATE TABLE #Results (Id INT NOT NULL, Name VARCHAR(50) NOT NULL);
                        INSERT INTO #Results EXEC dbo.usp_Callee;
                    END;
                    """,
                NoncompliantExplanation: "dbo.usp_Callee's real result set has exactly 1 column (Id), but #Results declares 2 (Id, Name) - INSERT ... EXEC binds positionally, so this statement raises a hard runtime error (Msg 213/8164) every single time dbo.usp_Caller runs.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_Caller AS
                    BEGIN
                        CREATE TABLE #Results (Id INT NOT NULL);
                        INSERT INTO #Results EXEC dbo.usp_Callee;
                    END;
                    """,
                CompliantExplanation: "#Results now declares exactly the 1 column dbo.usp_Callee's own result set actually returns, matching by position as INSERT ... EXEC requires."),
        ]);
}
