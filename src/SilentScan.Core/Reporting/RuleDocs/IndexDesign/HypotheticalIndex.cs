using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class HypotheticalIndex
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.HypotheticalIndex);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A Database Engine Tuning Advisor or missing-index-wizard leftover - identified directly
            from `sys.indexes.is_hypothetical`, the precise engine flag, never a `_dta_`-name-prefix
            heuristic that could miss a renamed one or false-positive on an unrelated real index
            sharing that naming convention. A hypothetical index has no real data behind it at all;
            it exists purely so a what-if tuning analysis session could reason about it, and is meant
            to be dropped once that session ends. One left behind in a live schema is pure clutter
            with zero query benefit - nobody chose to keep it as a design decision, it's simply a
            cleanup step that never happened.
            """,
        HowToFixIt: """
            Drop the hypothetical index - it has no real data behind it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A hypothetical index left behind from a tuning session",
                NoncompliantSql: """
                    -- Left behind by a Database Engine Tuning Advisor session:
                    -- an index that exists only in metadata (sys.indexes.is_hypothetical = 1),
                    -- with no real underlying data.
                    """,
                NoncompliantExplanation: "A hypothetical index provides no query benefit whatsoever - it's a what-if artifact from a tuning session, not a real, usable index, and its presence is pure clutter in the schema's own index list.",
                CompliantSql: """
                    DROP INDEX IX_HypotheticalArtifact ON dbo.Orders;
                    """,
                CompliantExplanation: "Dropping the hypothetical index removes the clutter entirely - nothing real was ever served by it."),
        ]);
}
