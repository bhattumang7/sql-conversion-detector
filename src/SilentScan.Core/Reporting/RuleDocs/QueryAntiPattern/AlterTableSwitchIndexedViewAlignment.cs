using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchIndexedViewAlignment
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            When a schema-bound indexed view is built on top of a partitioned table, the engine
            requires the view's own clustered index to be partitioned too, using the same scheme
            as its base table. If it isn't, ALTER TABLE ... SWITCH on that table fails outright
            with error 11401, regardless of how the two tables involved in the SWITCH otherwise
            compare.

            Separately, the engine requires that every indexed view referencing the target table
            has at least one corresponding indexed view referencing the source table - it checks
            the raw reference count first (error 11402) before ever looking at whether the views
            actually line up. A source table with fewer indexed views built on it than the target
            table fails this count check unconditionally, so it's decidable without reasoning
            about whether any of those views' partitioning actually aligns.

            Even when a referencing indexed view's clustered index is itself partitioned, its
            partitioning column must be a direct selection of the base table's own partitioning
            column - not an expression derived from it (error 11403), and not a direct selection
            of some other column (error 11405). And the partition scheme it's partitioned on must
            be built on a partition function equivalent to the base table's own partition
            function - equivalent by structure (range direction, parameter type, and boundary
            values), not merely by scheme name, since a view and its base table are free to pick
            different scheme names over the same function (error 11400).
            """,
        HowToFixIt: """
            Give every indexed view that references a partitioned table its own partitioned
            clustered index, using the same partition scheme as the table (or a different scheme
            built on an equivalent partition function), keyed on a column that's a direct
            selection of the table's own partitioning column. And before switching a partition
            into a table that's referenced by one or more indexed views, make sure the source
            table is referenced by at least as many indexed views as the target table.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Target table's indexed view has no counterpart on the source table",
                NoncompliantSql: """
                    CREATE VIEW dbo.OrdersTargetTotals WITH SCHEMABINDING AS
                    SELECT Grp, Id, Val FROM dbo.OrdersTarget;
                    CREATE UNIQUE CLUSTERED INDEX IX_OrdersTargetTotals ON dbo.OrdersTargetTotals(Grp, Id) ON PsOrders(Grp);

                    ALTER TABLE dbo.OrdersSource SWITCH PARTITION 2 TO dbo.OrdersTarget PARTITION 2;
                    -- Msg 11402: Target table 'OrdersTarget' is referenced by 1 indexed view(s),
                    -- but source table 'OrdersSource' is only referenced by 0 indexed view(s).
                    """,
                NoncompliantExplanation: "The target table is referenced by one indexed view and the source table by none - the engine's reference-count check fails before it ever evaluates whether the view would even align.",
                CompliantSql: """
                    CREATE VIEW dbo.OrdersSourceTotals WITH SCHEMABINDING AS
                    SELECT Grp, Id, Val FROM dbo.OrdersSource;
                    CREATE UNIQUE CLUSTERED INDEX IX_OrdersSourceTotals ON dbo.OrdersSourceTotals(Grp, Id) ON PsOrders(Grp);

                    ALTER TABLE dbo.OrdersSource SWITCH PARTITION 2 TO dbo.OrdersTarget PARTITION 2;
                    """,
                CompliantExplanation: "The source table now has a matching indexed view of its own, so the reference counts line up and the SWITCH proceeds to the engine's remaining checks."),
            new RuleDocExample(
                Title: "Indexed view's partitioning column is derived from an expression",
                NoncompliantSql: """
                    CREATE VIEW dbo.OrdersSourceTotals WITH SCHEMABINDING AS
                    SELECT Grp + 0 AS GrpKey, Id, Val FROM dbo.OrdersSource;
                    CREATE UNIQUE CLUSTERED INDEX IX_OrdersSourceTotals ON dbo.OrdersSourceTotals(GrpKey, Id) ON PsOrders(GrpKey);

                    ALTER TABLE dbo.OrdersSource SWITCH PARTITION 2 TO dbo.OrdersTarget PARTITION 2;
                    -- Msg 11403: Indexed view 'OrdersSourceTotals' is not aligned with table
                    -- 'OrdersSource'. The partitioning column 'GrpKey' calculates its value from
                    -- one or more columns or an expression, rather than directly selecting from
                    -- the table partitioning column 'Grp'.
                    """,
                NoncompliantExplanation: "The view's partitioning column is `Grp + 0`, an expression - even though it's always equal to `Grp`, the engine requires a bare column reference, not a computed one.",
                CompliantSql: """
                    CREATE VIEW dbo.OrdersSourceTotals WITH SCHEMABINDING AS
                    SELECT Grp, Id, Val FROM dbo.OrdersSource;
                    CREATE UNIQUE CLUSTERED INDEX IX_OrdersSourceTotals ON dbo.OrdersSourceTotals(Grp, Id) ON PsOrders(Grp);

                    ALTER TABLE dbo.OrdersSource SWITCH PARTITION 2 TO dbo.OrdersTarget PARTITION 2;
                    """,
                CompliantExplanation: "The view now directly selects `Grp` - the table's own partitioning column - with no expression in between, so this check passes."),
            new RuleDocExample(
                Title: "Indexed view's clustered index sits on a non-equivalent partition function",
                NoncompliantSql: """
                    CREATE PARTITION FUNCTION PfOrders (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
                    CREATE PARTITION SCHEME PsOrders AS PARTITION PfOrders ALL TO ([PRIMARY]);
                    CREATE PARTITION FUNCTION PfOrdersView (int) AS RANGE LEFT FOR VALUES (10, 20, 30, 999);
                    CREATE PARTITION SCHEME PsOrdersView AS PARTITION PfOrdersView ALL TO ([PRIMARY]);
                    -- dbo.OrdersSource is partitioned ON PsOrders(Grp)

                    CREATE VIEW dbo.OrdersSourceTotals WITH SCHEMABINDING AS
                    SELECT Grp, Id, Val FROM dbo.OrdersSource;
                    CREATE UNIQUE CLUSTERED INDEX IX_OrdersSourceTotals ON dbo.OrdersSourceTotals(Grp, Id) ON PsOrdersView(Grp);

                    ALTER TABLE dbo.OrdersSource SWITCH PARTITION 2 TO dbo.OrdersTarget PARTITION 2;
                    -- Msg 11400: Index 'IX_OrdersSourceTotals' on indexed view 'OrdersSourceTotals'
                    -- uses partition function 'PfOrdersView', but table 'OrdersSource' uses
                    -- non-equivalent partition function 'PfOrders'.
                    """,
                NoncompliantExplanation: "PfOrders and PfOrdersView have different boundary values, so they aren't equivalent, even though the schemes both map every partition to PRIMARY.",
                CompliantSql: """
                    CREATE PARTITION FUNCTION PfOrders (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
                    CREATE PARTITION SCHEME PsOrders AS PARTITION PfOrders ALL TO ([PRIMARY]);
                    CREATE PARTITION SCHEME PsOrdersViewAlias AS PARTITION PfOrders ALL TO ([PRIMARY]);
                    -- dbo.OrdersSource is partitioned ON PsOrders(Grp)

                    CREATE VIEW dbo.OrdersSourceTotals WITH SCHEMABINDING AS
                    SELECT Grp, Id, Val FROM dbo.OrdersSource;
                    CREATE UNIQUE CLUSTERED INDEX IX_OrdersSourceTotals ON dbo.OrdersSourceTotals(Grp, Id) ON PsOrdersViewAlias(Grp);

                    ALTER TABLE dbo.OrdersSource SWITCH PARTITION 2 TO dbo.OrdersTarget PARTITION 2;
                    """,
                CompliantExplanation: "PsOrdersViewAlias is a differently-named scheme, but it's built on the same partition function (PfOrders) as the table's own scheme, so the two are equivalent and this check passes."),
        ]);
}
