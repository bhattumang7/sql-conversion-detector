using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class TriggerOrder
{
    public static string RuleId => SarifRuleCatalog.TriggerOrderRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table carries two or more enabled AFTER triggers for the same firing event (INSERT,
            UPDATE, or DELETE) with no sp_settriggerorder pin narrowing their relative order down to
            a single pair. SQL Server documents that firing order among unpinned triggers on the
            same event is undefined - oracle-confirmed via sys.trigger_events.is_first/is_last,
            which report the real pin state directly rather than a guess. When two triggers on the
            same event both touch overlapping state (writing to the same audit table, incrementing
            the same counter, validating data the other trigger also modifies), which one runs first
            silently determines the outcome - and that outcome can change across a restart, a
            plan-cache eviction, or simply because the engine's internal tie-break happens to differ.
            This is catalog-only: the finding is detected from the table's own trigger metadata,
            without needing to see any query that touches the table.
            """,
        HowToFixIt: """
            Pin the triggers with sp_settriggerorder ('First' or 'Last') so their relative order is
            no longer ambiguous, or consolidate the overlapping logic into a single trigger so no two
            triggers on the same event are left with an undefined relative order.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two AFTER INSERT triggers with no order pinned",
                NoncompliantSql: """
                    CREATE TRIGGER dbo.trg_Orders_Audit ON dbo.Orders AFTER INSERT AS
                        INSERT INTO dbo.OrderAudit (OrderId) SELECT OrderId FROM inserted;

                    CREATE TRIGGER dbo.trg_Orders_Validate ON dbo.Orders AFTER INSERT AS
                        UPDATE dbo.Orders SET Status = 'Validated' WHERE OrderId IN (SELECT OrderId FROM inserted);
                    """,
                NoncompliantExplanation: "Both triggers fire on the same event (AFTER INSERT) with neither pinned as first or last - if trg_Orders_Audit needs to see the pre-validation Status, its relative order against trg_Orders_Validate is undefined.",
                CompliantSql: """
                    EXEC sp_settriggerorder @triggername = 'dbo.trg_Orders_Validate', @order = 'First', @stmttype = 'INSERT';
                    """,
                CompliantExplanation: "Pinning trg_Orders_Validate to run first makes the two triggers' relative order an explicit, engine-enforced fact instead of an undefined one."),
        ]);
}
