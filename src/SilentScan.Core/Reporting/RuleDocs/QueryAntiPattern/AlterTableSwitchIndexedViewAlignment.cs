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
            """,
        HowToFixIt: """
            Give every indexed view that references a partitioned table its own partitioned
            clustered index, using the same partition scheme as the table. And before switching a
            partition into a table that's referenced by one or more indexed views, make sure the
            source table is referenced by at least as many indexed views as the target table.
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
        ]);
}
