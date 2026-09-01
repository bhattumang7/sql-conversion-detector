using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class ExecResultSetsShapeColumnCountMismatch
{
    public static string RuleId => SarifRuleCatalog.ExecResultSetsShapeColumnCountMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The `WITH RESULT SETS` clause of `EXEC` re-declares the shape of the executed
            procedure's own result set - the engine binds that declaration to the procedure's
            actual, real result set purely by POSITION, never by name. That's an implicit
            assumption every such statement makes: that the procedure's real, engine-described
            first result set matches the clause's own declared column list one-for-one in order.
            When the procedure's real column COUNT differs from that declared count, T-SQL raises a
            hard, immediate runtime error (Msg 11537, "its WITH RESULT SETS clause specified N
            column(s) ..., but the statement sent M column(s) at run time") the instant the
            statement executes - live-verified directly against
            `sys.dm_exec_describe_first_result_set` (compile-only), the same real,
            engine-authoritative result-set description the `insert-exec-temp-table` family already
            relies on.

            Unlike the sibling `exec-with-result-sets-column-type-mismatch` finding, this is not
            itself a SILENT defect - the engine fails loudly, every time. It's still worth
            reporting, because it names a query this tool can PROVE fails at runtime on every
            single execution, a stronger and more actionable claim than static analysis can
            normally make about runtime behavior at all.
            """,
        HowToFixIt: """
            Make the WITH RESULT SETS clause's own declared column count match the executed
            procedure's real first result-set column count exactly. WITH RESULT SETS binds purely
            by position.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A WITH RESULT SETS clause declaring fewer columns than the procedure returns",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_Callee AS
                    BEGIN
                        SELECT 1 AS Id, 'x' AS Name;
                    END;

                    CREATE PROCEDURE dbo.usp_Caller AS
                    BEGIN
                        EXEC dbo.usp_Callee WITH RESULT SETS ((Id INT NOT NULL));
                    END;
                    """,
                NoncompliantExplanation: "dbo.usp_Callee's real result set has 2 columns (Id, Name), but the WITH RESULT SETS clause declares only 1 - the binding is positional, so this statement raises a hard runtime error (Msg 11537) every single time dbo.usp_Caller runs.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_Caller AS
                    BEGIN
                        EXEC dbo.usp_Callee WITH RESULT SETS ((Id INT NOT NULL, Name VARCHAR(10) NOT NULL));
                    END;
                    """,
                CompliantExplanation: "The WITH RESULT SETS clause now declares exactly the 2 columns dbo.usp_Callee's own result set actually returns, matching by position as the clause requires."),
        ]);
}
