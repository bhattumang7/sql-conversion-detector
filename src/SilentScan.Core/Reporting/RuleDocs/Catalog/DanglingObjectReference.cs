using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class DanglingObjectReference
{
    public static string RuleId => SarifRuleCatalog.DanglingObjectReferenceRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server defers name resolution for a module body (a stored procedure, view,
            function, or trigger) until the statement that reaches a given reference actually
            runs - `CREATE PROCEDURE dbo.MyProc AS SELECT * FROM dbo.TableThatDoesNotExist`
            compiles and creates the procedure without complaint, even though `dbo.TableThatDoesNotExist`
            has never existed. The gap between "this compiled" and "this reference is real" stays
            invisible until the first call that actually reaches it, which fails immediately with
            Msg 208, "Invalid object name" - a runtime error in a module that looked completely
            clean at deploy time.

            This is exactly the silent-until-production shape this tool exists to catch: nothing
            in the module's own text or its successful `CREATE`/`ALTER` gives any indication that
            a reference is broken, and a code path that isn't exercised by every deploy's smoke
            test can carry a dangling reference for a long time before a real caller finds it. The
            fix is a genuine catalog fact, not a guess: SQL Server's own binder either can resolve
            a name to a real object or it can't, and this rule reports only the case where it
            can't, confirmed against the engine's live answer right now (not a possibly-stale
            cached dependency snapshot) before being reported at all.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure that outlived a table rename",
                NoncompliantSql: """
                    CREATE TABLE dbo.CustomerOrders (OrderId INT NOT NULL, CustomerId INT NOT NULL);
                    GO
                    CREATE PROCEDURE dbo.GetCustomerOrderCount
                        @CustomerId INT
                    AS
                    BEGIN
                        -- Renamed to dbo.CustomerOrders above; this procedure was never updated.
                        SELECT COUNT(*) FROM dbo.Orders WHERE CustomerId = @CustomerId;
                    END;
                    """,
                NoncompliantExplanation: "dbo.Orders no longer exists - CREATE PROCEDURE succeeds anyway because SQL Server defers name resolution, so this compiles clean and sits in the catalog looking correct until the first EXEC, which fails with Msg 208.",
                CompliantSql: """
                    CREATE TABLE dbo.CustomerOrders (OrderId INT NOT NULL, CustomerId INT NOT NULL);
                    GO
                    CREATE PROCEDURE dbo.GetCustomerOrderCount
                        @CustomerId INT
                    AS
                    BEGIN
                        SELECT COUNT(*) FROM dbo.CustomerOrders WHERE CustomerId = @CustomerId;
                    END;
                    """,
                CompliantExplanation: "The procedure references the table's real, current name - the engine's own binder resolves it to a real object, so no call site can ever hit Msg 208 for this reference."),
        ]);
}
