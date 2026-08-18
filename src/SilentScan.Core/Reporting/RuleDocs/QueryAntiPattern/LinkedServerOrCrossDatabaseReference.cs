using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class LinkedServerOrCrossDatabaseReference
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.LinkedServerOrCrossDatabaseReference);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server's cost-based optimizer picks a plan by comparing estimated costs across
            candidate plans, and those estimates are built from statistics the local engine
            maintains on its own tables and indexes. A four-part name (`LinkedServer.Database
            .Schema.Table`) references an object on a different, remote SQL Server instance
            entirely, and a three-part name (`Database.Schema.Table`) references an object in a
            different database than the one the current session/scan is connected to. In both
            cases, the object being referenced isn't the local optimizer's own catalog - it's
            somewhere the optimizer has only limited, and sometimes no, visibility into.

            For a genuine linked server, the optimizer generally has to ask the remote provider for
            whatever row-count and selectivity information it's willing to share (via OpenRowset/
            OLE DB provider statistics calls), which ranges from reasonably accurate to
            effectively a guess depending on the provider and how current its own statistics are -
            and network round-trips for that negotiation are themselves a cost the optimizer has
            imperfect visibility into. For a cross-database but same-instance reference, the
            optimizer can usually see the target database's statistics directly (assuming
            permissions and that the database is online), but the plan is still built by treating
            that access as materially different from an access to an object in the connected
            database, and any distributed-query aspects of the plan carry the same weaker
            cost-estimation guarantees as the linked-server case.

            The practical effect is that any predicate, join, or aggregation touching a
            linked-server or cross-database object carries a cardinality estimate that's on
            noticeably less solid ground than the same operation against a local, same-database
            object - not necessarily wrong, but resting on statistics the optimizer doesn't fully
            own and can't always verify are current. A join plan chosen confidently for a local
            table can be chosen far less confidently, or simply guessed, the moment one side of
            that join is remote or in another database.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A query joining a local table to a linked-server table",
                NoncompliantSql: """
                    CREATE TABLE dbo.LocalOrders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);

                    SELECT o.OrderId, c.CustomerName
                    FROM dbo.LocalOrders AS o
                    JOIN RemoteServer.CustomerDb.dbo.Customers AS c
                        ON c.CustomerId = o.CustomerId;
                    """,
                NoncompliantExplanation: "RemoteServer.CustomerDb.dbo.Customers is a four-part linked-server reference - the optimizer's cardinality estimate for the join depends on whatever statistics the remote provider is willing and able to supply, which is materially less reliable than statistics on a local, same-database table."),
        ]);
}
