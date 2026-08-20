using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Hint;

internal static class IndexDoesNotExist
{
    public static string RuleId => SarifRuleCatalog.IndexHintRuleId(IndexHintFindingKind.IndexDoesNotExist);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `WITH (INDEX(IndexName))` table hint names a specific index by identifier - and if
            that name doesn't match any index the catalog actually has for that table, the query
            doesn't silently fall back to a normal access path. It's a hard compile error every
            single time the statement runs: SQL Server raises Msg 308, "Index '...' on table '...'
            (specified in the FROM clause) does not exist." Oracle-confirmed directly against a real
            seeded index.

            The realistic cause is a migration that dropped or renamed an index without updating
              every hint site that still names the old identifier - the hint and the index used to
            agree, a schema change broke that agreement, and nothing about the query text itself
            looks wrong until it's actually executed. This rule catches it statically, before
            anything runs: the exact broken hint and table can be named across an entire codebase at
            once, rather than waiting to discover it the first time this code path executes in
            production.
            """,
        HowToFixIt: """
            Correct the INDEX(...) hint to name a real index that exists in the catalog for this
            table, or remove the hint entirely if the intent was only ever to suggest an access path
            rather than force one.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A hint naming an index that was dropped or renamed",
                NoncompliantSql: """
                    -- dbo.Orders has IX_Orders_Status, not IX_DoesNotExist
                    SELECT OrderId FROM dbo.Orders WITH (INDEX(IX_DoesNotExist)) WHERE OrderId = 1;
                    """,
                NoncompliantExplanation: "No index named IX_DoesNotExist exists on dbo.Orders - this statement fails to compile with Msg 308 every time it runs, not merely under some data shapes.",
                CompliantSql: """
                    SELECT OrderId FROM dbo.Orders WITH (INDEX(IX_Orders_Status)) WHERE OrderId = 1;
                    """,
                CompliantExplanation: "IX_Orders_Status is a real index on dbo.Orders, so the hint resolves and the statement compiles."),
        ]);
}
