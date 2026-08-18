using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class GlobalCursorDeclaration
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.GlobalCursorDeclaration);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            T-SQL's CURSOR declaration syntax accepts an explicit LOCAL or GLOBAL keyword, and when
            neither is written, SQL Server falls back to whatever the database's own
            CURSOR_DEFAULT setting is - which is GLOBAL out of the box, unless someone has changed
            it. A GLOBAL cursor isn't scoped to the batch, stored procedure, or trigger that
            declared it; it's scoped to the entire connection, and stays visible and open under its
            declared name for as long as that connection lives, or until it's explicitly
            deallocated. A LOCAL cursor, by contrast, goes out of scope and is implicitly
            deallocated the moment the batch/procedure/trigger that declared it ends.

            The practical risk is twofold. First, a naming collision: two stored procedures that
            each declare a cursor named `cur` without LOCAL, called from the same connection (one
            calling the other, or a connection-pooled client reusing the same session across
            calls), can collide - the second DECLARE CURSOR fails because a cursor by that name is
            already open from the first, or worse, code that expects a fresh cursor silently
            reuses/reopens one left behind by an earlier, unrelated call on the same connection.
            Second, a resource leak: a GLOBAL cursor that's opened but never explicitly closed and
            deallocated (for instance because an error path skips the cleanup) stays allocated on
            the connection - not per-call, but for the connection's whole lifetime - which matters
            especially with connection pooling, where the same physical connection serves many
            logical callers in sequence.

            None of this is intentional in the overwhelming majority of cases: almost every cursor
            written in application stored-procedure code is meant to live and die within the
            procedure that declared it, and the GLOBAL default is a silent trap for exactly that
            common case, not a deliberate choice.
            """,
        HowToFixIt: """
            Add LOCAL explicitly to the cursor declaration. This makes the scoping behavior
            independent of the database's CURSOR_DEFAULT setting (which can differ across
            environments and can be changed by someone else later) and guarantees the cursor is
            deallocated automatically when the declaring batch/procedure/trigger ends, removing
            both the naming-collision and the leak risk without changing anything else about how
            the cursor behaves.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A cursor declared without LOCAL",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers (CustomerId INT NOT NULL PRIMARY KEY, Region VARCHAR(20) NOT NULL);

                    CREATE PROCEDURE dbo.ProcessCustomers
                    AS
                    BEGIN
                        DECLARE @CustomerId INT;

                        DECLARE cur CURSOR FOR
                            SELECT CustomerId FROM dbo.Customers WHERE Region = 'West';

                        OPEN cur;
                        FETCH NEXT FROM cur INTO @CustomerId;

                        WHILE @@FETCH_STATUS = 0
                        BEGIN
                            FETCH NEXT FROM cur INTO @CustomerId;
                        END;

                        CLOSE cur;
                        DEALLOCATE cur;
                    END;
                    """,
                NoncompliantExplanation: "With no LOCAL/GLOBAL keyword, the cursor's scope depends on the database's CURSOR_DEFAULT setting - if that's GLOBAL (the out-of-the-box default), `cur` is visible connection-wide and a second call to this same procedure on the same pooled connection, or another procedure declaring a cursor also named `cur`, can collide with it.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.ProcessCustomers
                    AS
                    BEGIN
                        DECLARE @CustomerId INT;

                        DECLARE cur CURSOR LOCAL FOR
                            SELECT CustomerId FROM dbo.Customers WHERE Region = 'West';

                        OPEN cur;
                        FETCH NEXT FROM cur INTO @CustomerId;

                        WHILE @@FETCH_STATUS = 0
                        BEGIN
                            FETCH NEXT FROM cur INTO @CustomerId;
                        END;

                        CLOSE cur;
                        DEALLOCATE cur;
                    END;
                    """,
                CompliantExplanation: "LOCAL makes the scoping explicit and independent of the database setting - `cur` is guaranteed to be deallocated when the procedure ends, and can't collide with a same-named cursor declared elsewhere on the same connection."),
        ]);
}
