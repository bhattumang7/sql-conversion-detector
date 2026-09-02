using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Correctness;

internal static class ViewCheckOptionContradiction
{
    public static string RuleId => SarifRuleCatalog.ViewCheckOptionContradictionRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `WITH CHECK OPTION` makes a view enforce its own `WHERE` clause on every row written
            through it: an `INSERT`/`UPDATE` that would produce a row failing that predicate is
            rejected at execution, not silently accepted. When the view's `WHERE` clause constrains a
            single column to a literal-comparable range (a comparison, `BETWEEN`, or an AND/OR
            combination of those) and the statement itself assigns a literal for that column outside
            that range, the rejection is certain before the statement ever runs - it doesn't depend on
            existing data, a trigger, or a concurrent writer.

            Oracle-confirmed (Docker SQL Server 2022): `CREATE VIEW dbo.V AS SELECT id, amt FROM
            dbo.T WHERE amt > 10 WITH CHECK OPTION` followed by `INSERT INTO dbo.V (id, amt) VALUES
            (1, 5)` fails with Msg 550 ("the target view either specifies WITH CHECK OPTION ... and
            one or more rows resulting from the operation did not qualify under the CHECK OPTION
            constraint").
            """,
        HowToFixIt: "Correct the literal so the resulting row satisfies the view's WHERE clause, or write the row through the underlying base table instead of the view.",
        Examples:
        [
            new RuleDocExample(
                Title: "INSERT literal outside the view's own WHERE range",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Amount INT NOT NULL);
                    CREATE VIEW dbo.ActiveOrders AS
                        SELECT OrderId, Amount FROM dbo.Orders WHERE Amount > 10
                        WITH CHECK OPTION;

                    INSERT INTO dbo.ActiveOrders (OrderId, Amount) VALUES (1, 5);
                    """,
                NoncompliantExplanation: "Amount = 5 can never satisfy the view's own Amount > 10 predicate, so this INSERT always fails with Msg 550.",
                CompliantSql: """
                    INSERT INTO dbo.ActiveOrders (OrderId, Amount) VALUES (1, 15);
                    """,
                CompliantExplanation: "Amount = 15 satisfies Amount > 10, so the row qualifies under the view's CHECK OPTION constraint."),
        ]);
}
