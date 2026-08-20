using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Hint;

internal static class HintedIndexNotSeekable
{
    public static string RuleId => SarifRuleCatalog.IndexHintRuleId(IndexHintFindingKind.HintedIndexNotSeekable);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An `INDEX(...)` table hint doesn't merely suggest an access path to the optimizer - per
            T-SQL's own documented semantics, it FORCES the engine to use exactly that index, with no
            fallback. That's fine when the statement's own predicate actually binds the hinted
            index's leading key column, since the engine can still descend the index's B-tree to a
            useful starting point. When nothing in the statement references that leading column at
            all, the engine has no way to seek into the index - it's forced to use it anyway, and the
            only way to satisfy that is a full Index Scan reading every row, typically followed by a
            bookmark lookup back to the real access path the query actually needed.

            Oracle-confirmed directly against a real seeded index: the identical query with no hint
            produces a clean Clustered Index Seek; adding the hint against a predicate that never
            touches the hinted index's leading column degrades the same query to an Index Scan plus
            Nested Loops; hinting an index whose leading column IS bound by the query's own predicate
            stays a clean seek even through the hint - confirming it's the leading-column binding,
            not the presence of a hint per se, that decides seek versus scan. This shares its
            "is the leading column bound anywhere" check with this tool's composite-index
            leading-column rule, deliberately the same conservative, liberal-to-suppress test
            generalized to single-column indexes.
            """,
        HowToFixIt: """
            Bind the hinted index's own leading key column somewhere in the statement (a predicate,
            a join condition), or hint a different index whose leading column is actually usable
            given what the statement references.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A hint forcing an index whose leading column is never bound",
                NoncompliantSql: """
                    -- IX_Orders_Status's leading key column is Status
                    SELECT OrderId FROM dbo.Orders WITH (INDEX(IX_Orders_Status)) WHERE OrderId = 1;
                    """,
                NoncompliantExplanation: "The predicate filters on OrderId, never Status - the engine is forced to use IX_Orders_Status but can't seek into it, degrading to a full Index Scan plus a lookup back to find OrderId = 1.",
                CompliantSql: """
                    SELECT OrderId FROM dbo.Orders WITH (INDEX(IX_Orders_Status)) WHERE Status = 5;
                    """,
                CompliantExplanation: "The predicate now filters on Status, IX_Orders_Status's own leading column - the engine can seek directly into the forced index."),
        ]);
}
