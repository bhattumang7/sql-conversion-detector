using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Join;

internal static class CartesianCrossJoin
{
    public static string RuleId => SarifRuleCatalog.CartesianJoinRuleId(CartesianJoinKind.ExplicitCrossJoin);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An explicit `CROSS JOIN` self-documents a deliberate cartesian product between its two
            operands - unlike the legacy comma-join, the author had to type the word CROSS, so this
            finding is reported at Medium confidence rather than the comma-join's default, and exists
            to catch the less common but still real case where a CROSS JOIN was left over from
            debugging, copied from another query without adjusting it, or intended as a temporary
            placeholder for a real join condition that never got added.

            The connectivity check is the same graph-reachability analysis the comma-join rule uses:
            a CROSS JOIN between two tables is only flagged when those two tables remain
            disconnected from the rest of the FROM clause's own predicate graph even after every
            other join/WHERE condition in the statement is accounted for - a CROSS JOIN feeding into
            a table that a later predicate connects back to the rest of the query is not a defect at
            all, since the effective result set is still constrained. As with the comma-join rule,
            an unqualified column reference anywhere in the statement's predicates makes the whole
            FROM clause decline rather than risk a wrong attribution.
            """,
        HowToFixIt: """
            Confirm the CROSS JOIN is genuinely intended as an unconstrained cartesian product (a
            row-generator pattern, or a deliberate all-pairs report); if not, replace it with a JOIN
            carrying an ON condition that connects the two tables the way the rest of the query
            expects.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A leftover CROSS JOIN with no connecting predicate",
                NoncompliantSql: """
                    SELECT p.ProductName, w.WarehouseName
                    FROM dbo.Products AS p
                    CROSS JOIN dbo.Warehouses AS w;
                    """,
                NoncompliantExplanation: "No predicate anywhere in the statement relates Products to Warehouses, so every product is paired with every warehouse - if this was meant to list each product's actual stock locations rather than every possible pairing, the result silently over-reports.",
                CompliantSql: """
                    SELECT p.ProductName, w.WarehouseName
                    FROM dbo.Products AS p
                    JOIN dbo.Inventory AS i ON i.ProductId = p.ProductId
                    JOIN dbo.Warehouses AS w ON w.WarehouseId = i.WarehouseId;
                    """,
                CompliantExplanation: "Routing through the Inventory table connects Products to Warehouses through a real relationship, so only actual stock locations are returned."),
        ]);
}
