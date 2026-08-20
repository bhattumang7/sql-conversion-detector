using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class TableValuedFunctionReturnUsesDatabaseCollation
{
    public static string RuleId => SarifRuleCatalog.ModuleCompileFlagRuleId(ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A non-schema-bound table-valued function's own `RETURNS @t TABLE(...)` declaration can
            include a character-typed column with no explicit `COLLATE` clause - when that happens,
            SQL Server resolves that column's collation against the CURRENT database's default
            collation at `CREATE`/`ALTER` time and bakes it in permanently, a fact directly readable
            from `sys.sql_modules.uses_database_collation`. The problem surfaces later: if the
            database's own default collation is ever changed via `ALTER DATABASE ... COLLATE`, the
            function's already-compiled return shape keeps its OLD, now-stale collation - it silently
            disagrees with the database's new default, exactly the kind of collation mismatch that
            can force an unexpected implicit conversion or even a hard collation-conflict compile
            error (Msg 468) wherever the function's output is later compared against something using
            the new default.

            Schema-bound modules are deliberately excluded from this finding: this tool oracle-
            confirmed directly that schema-binding sets `uses_database_collation` unconditionally,
            regardless of whether the module touches any string data at all, so the flag carries no
            differentiating signal for schema-bound modules - it would fire on every single one of
            them, providing no actual information.
            """,
        HowToFixIt: """
            Add an explicit COLLATE clause to the table-valued function's own RETURNS TABLE column
            declaration instead of relying on the database's default collation at CREATE/ALTER time.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A TVF's RETURNS TABLE column with no explicit COLLATE clause",
                NoncompliantSql: """
                    CREATE FUNCTION dbo.fn_ActiveCustomerNames()
                    RETURNS @Result TABLE (Name VARCHAR(100))
                    AS
                    BEGIN
                        INSERT INTO @Result SELECT Name FROM dbo.Customers WHERE IsActive = 1;
                        RETURN;
                    END;
                    """,
                NoncompliantExplanation: "Name's collation was implicitly resolved against the database's default collation at CREATE time and baked in - a later ALTER DATABASE ... COLLATE leaves this function's return shape silently disagreeing with the database's new default.",
                CompliantSql: """
                    CREATE FUNCTION dbo.fn_ActiveCustomerNames()
                    RETURNS @Result TABLE (Name VARCHAR(100) COLLATE Latin1_General_CI_AS)
                    AS
                    BEGIN
                        INSERT INTO @Result SELECT Name FROM dbo.Customers WHERE IsActive = 1;
                        RETURN;
                    END;
                    """,
                CompliantExplanation: "The explicit COLLATE clause fixes the return column's collation permanently, independent of whatever the database's own default collation is or later becomes."),
        ]);
}
