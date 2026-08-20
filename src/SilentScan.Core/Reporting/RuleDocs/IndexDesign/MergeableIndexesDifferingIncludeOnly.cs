using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class MergeableIndexesDifferingIncludeOnly
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Two active indexes that share an identical key-column list, the same per-column sort
            direction, and the same uniqueness/kind - but carry different, genuinely
            non-overlapping INCLUDE column sets (neither index's INCLUDE list is a subset of the
            other's) - each look individually legitimate, likely built at different times for
            different queries. But they're mergeable into a single index carrying the union of both
            INCLUDE lists, at no seek cost to either original query, for meaningfully less write and
            storage overhead than maintaining both separately.

            This is deliberately distinct from the sibling `duplicate-index` rule (identical key list
            AND identical INCLUDE list - true duplicates) and `subsumed-index` (a proper key-list
            prefix relationship) - the divergence here is only in the INCLUDE columns. It's also only
            ever compared when both indexes' own sort direction is genuinely known from a live
            catalog read; an unknown sort direction on either side means this pass cannot confirm
            the two indexes truly match, and never guesses that they do.
            """,
        HowToFixIt: """
            Merge the two indexes into one carrying the union of their INCLUDE columns.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two indexes sharing a key but carrying different INCLUDE columns",
                NoncompliantSql: """
                    CREATE NONCLUSTERED INDEX IX_A ON dbo.Customers (CustomerId) INCLUDE (Email);
                    CREATE NONCLUSTERED INDEX IX_B ON dbo.Customers (CustomerId) INCLUDE (Phone);
                    """,
                NoncompliantExplanation: "Both indexes share the identical key (CustomerId), same sort direction, but IX_A includes only Email and IX_B only Phone - neither is a subset of the other, so both are maintained separately at full write/storage cost even though they could be one index.",
                CompliantSql: """
                    CREATE NONCLUSTERED INDEX IX_Customers_CustomerId ON dbo.Customers (CustomerId) INCLUDE (Email, Phone);
                    """,
                CompliantExplanation: "One index carrying the union of both INCLUDE lists serves both original queries at the same seek cost, for less write/storage overhead than maintaining two separate indexes."),
        ]);
}
