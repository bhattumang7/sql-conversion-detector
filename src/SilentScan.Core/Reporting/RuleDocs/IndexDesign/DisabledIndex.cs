using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class DisabledIndex
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.DisabledIndex);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An `ALTER INDEX ... DISABLE` left in place still occupies catalog metadata and blocks a
            same-named `CREATE INDEX` from being created, but serves no query the engine actually
            runs today - a disabled index is unusable until it's rebuilt. Left forgotten, it's
            clutter that can silently confuse anyone reading the table's own index list into
            believing coverage exists that doesn't, and blocks recreating an index under that same
            name until the disabled one is either rebuilt or dropped first.

            This never fires on a hypothetical index (the sibling `hypothetical-index` rule's own
            target) - the two are structurally distinct catalog states this pass reads separately,
            so a row is never double-reported under both kinds.
            """,
        HowToFixIt: """
            Rebuild the disabled index to make it usable again, or drop it if it's no longer needed.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An index left disabled",
                NoncompliantSql: """
                    ALTER INDEX IX_Orders_Status ON dbo.Orders DISABLE;
                    """,
                NoncompliantExplanation: "IX_Orders_Status is now unusable by the engine but still occupies catalog metadata and blocks a same-named CREATE INDEX - a real, if silent, source of confusion for anyone assuming this index provides seek coverage.",
                CompliantSql: """
                    ALTER INDEX IX_Orders_Status ON dbo.Orders REBUILD;
                    """,
                CompliantExplanation: "Rebuilding restores the index to a usable state. (If the index is no longer needed at all, DROP INDEX removes the clutter entirely instead.)"),
        ]);
}
