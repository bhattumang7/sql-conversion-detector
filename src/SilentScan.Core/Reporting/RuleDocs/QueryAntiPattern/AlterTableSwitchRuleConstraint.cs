using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchRuleConstraint
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchRuleConstraint);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            CREATE RULE plus sp_bindrule is a legacy, pre-CHECK-constraint way to enforce a column
            value restriction - it still works today for backward compatibility, but Microsoft has
            deprecated it in favor of CHECK constraints for a long time. ALTER TABLE ... SWITCH
            refuses outright if either the source or the target table has one bound to any column,
            regardless of which side it's on - unlike most of the other SWITCH prerequisites, this
            one isn't about the two tables matching each other; a single RULE binding anywhere is
            enough to block the statement.

            This is easy to overlook precisely because RULE is rare in modern codebases - a table
            that's carried one since a much older schema version can silently block a SWITCH that
            has nothing to do with the rule's own logic.
            """,
        HowToFixIt: """
            Unbind the RULE from the column with sp_unbindrule, or replace the RULE with an
            equivalent CHECK constraint (the modern, non-deprecated mechanism, which does not
            block SWITCH), before running the SWITCH.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A target table carrying a legacy RULE binding from an older schema version",
                NoncompliantSql: """
                    CREATE RULE dbo.Rule_PositiveAmount AS @value > 0;
                    GO
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL, Amount INT NOT NULL);
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, Amount INT NOT NULL);
                    EXEC sys.sp_bindrule 'dbo.Rule_PositiveAmount', 'dbo.Orders.Amount';
                    -- (Orders is partitioned; OrdersStaging holds a batch to load in.)

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    -- Msg 4964: table 'Orders' has RULE constraint 'Rule_PositiveAmount'. SWITCH
                    -- is not allowed on tables with RULE constraints.
                    """,
                NoncompliantExplanation: "The target table's Amount column has a legacy RULE bound to it - the engine refuses the SWITCH outright with error 4964, independent of anything about the source table.",
                CompliantSql: """
                    EXEC sys.sp_unbindrule 'dbo.Orders.Amount';
                    ALTER TABLE dbo.Orders WITH CHECK ADD CONSTRAINT CK_Orders_Amount CHECK (Amount > 0);

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    """,
                CompliantExplanation: "Replacing the RULE with an equivalent CHECK constraint removes the RULE-specific restriction entirely - the SWITCH proceeds to the engine's remaining checks (including the constraint-mismatch check, which the CHECK constraint would then need a matching counterpart on the source table for)."),
        ]);
}
