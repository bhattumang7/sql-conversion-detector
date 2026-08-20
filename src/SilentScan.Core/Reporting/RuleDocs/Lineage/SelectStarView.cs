using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Lineage;

internal static class SelectStarView
{
    public static string RuleId => SarifRuleCatalog.SelectStarViewRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A view or inline TVF nested one or more view/TVF layers deep whose own outermost query
            is a bare or qualified `SELECT *` has a column list that's frozen at `CREATE`/`ALTER`
            time and silently disagrees with the base table's real, current column list after any
            later schema change - a materially different, stronger claim than the plain "SELECT * is
            bad style" advice this tool deliberately declines to ship as its own rule. This finding
            fires specifically because that frozen list forces every consumer to carry the view's
            FULL width whether or not it actually needs it, which is exactly how a covering index
            stops covering: an index built to satisfy a consumer's own narrower column needs no
            longer helps once the view widens what the consumer is forced to read through it.

            This is confirmed directly against a real engine, not merely assumed from how catalog
            metadata caching usually works: a view's `SELECT *` column list stays frozen even through
            `sys.dm_exec_describe_first_result_set` (the same live, describe-only ground truth this
            tool's own live-parity gate otherwise trusts as authoritative) AND through a real
            execution of the view itself - not a stale catalog cache this tool's live-parity
            discipline already accounts for elsewhere, but a genuinely different, wrong current
            answer until `sp_refreshview` actually runs.

            This rule only fires when a real, different consuming query elsewhere explicitly selects
            a strict, named subset of the view's full column set - a consumer that itself does
            `SELECT *` never narrows anything by construction and is never matched, since there's no
            covering-index story to defeat if the consumer reads everything anyway. One finding is
            reported per (candidate view, consuming query site) pair, not deduplicated per view,
            since the actionable unit is "this specific consumer defeats this specific
            covering-index story" - a genuinely per-site concern.
            """,
        HowToFixIt: """
            Run sp_refreshview (or ALTER/CREATE the view) to resync its column metadata with the
            base table - avoid a bare SELECT * in a view definition to prevent this recurring.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A nested view's SELECT * forces a narrowing consumer to carry the full width",
                NoncompliantSql: """
                    CREATE VIEW dbo.vInner AS SELECT A, B, C FROM dbo.T;
                    CREATE VIEW dbo.vOuter AS SELECT * FROM dbo.vInner;

                    SELECT v.A FROM dbo.vOuter v;
                    """,
                NoncompliantExplanation: "dbo.vOuter's SELECT * is nested one view layer deep over dbo.vInner, and its column list is frozen at CREATE time; the consumer only needs column A, but is forced to read through vOuter's own full, potentially stale width - a later ALTER TABLE on dbo.T adding or dropping columns leaves vOuter silently disagreeing with dbo.T until sp_refreshview runs.",
                CompliantSql: """
                    CREATE VIEW dbo.vInner AS SELECT A, B, C FROM dbo.T;
                    CREATE VIEW dbo.vOuter AS SELECT A, B, C FROM dbo.vInner;

                    SELECT v.A FROM dbo.vOuter v;
                    """,
                CompliantExplanation: "dbo.vOuter names its columns explicitly instead of using SELECT * - its column list can no longer silently drift out of sync with dbo.T's real current shape, and a covering index built for the consumer's own narrower needs stays effective."),
        ]);
}
