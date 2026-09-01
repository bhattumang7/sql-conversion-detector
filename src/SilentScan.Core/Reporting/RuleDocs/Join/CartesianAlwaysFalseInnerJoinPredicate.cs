using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Join;

internal static class CartesianAlwaysFalseInnerJoinPredicate
{
    public static string RuleId => SarifRuleCatalog.CartesianJoinRuleId(CartesianJoinKind.AlwaysFalseInnerJoinPredicate);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An INNER JOIN only keeps a pair of rows when its own ON predicate evaluates to TRUE.
            When that predicate folds - from constant literals, or from a single column carrying two
            mutually exclusive literal constraints - to a condition that can never be TRUE for any
            pair of rows, the join can never match anything: the statement compiles and runs, but
            this join contributes zero rows every single time, regardless of what data the tables
            actually hold.

            This is the complementary defect to the shipped no-predicate cartesian-join family: that
            family fires when nothing connects two tables and the join matches far too many rows;
            this one fires when the join's own predicate rules out every row. Both are silent -
            neither raises an error - and both are the kind of defect that a small development
            dataset can mask (a `LEFT JOIN` upstream, or an early `TOP`, can make a zero-row inner
            join look like it's simply filtering normally) until the code is read carefully or the
            output is checked row by row.

            Only `INNER JOIN` is in scope: an outer join (`LEFT`/`RIGHT`/`FULL`) with the same
            always-false predicate still returns every row from its preserved side, null-extended -
            oracle-confirmed - so the same predicate is not a defect there.
            """,
        HowToFixIt: """
            Correct the ON predicate so it expresses the join key that was actually intended - most
            often a stray literal that was meant to reference the other table's column. If the join
            is genuinely meant to never match under current conditions, that intent belongs in the
            WHERE clause or application logic instead of a join that silently contributes nothing.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An INNER JOIN whose ON predicate is a literal contradiction",
                NoncompliantSql: "SELECT o.OrderId, d.Note FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON 1 = 0;",
                NoncompliantExplanation: "1 = 0 never evaluates to TRUE for any pair of rows, so this join returns zero rows every time it runs regardless of the tables' real data.",
                CompliantSql: "SELECT o.OrderId, d.Note FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON o.OrderId = d.OrderId;",
                CompliantExplanation: "Replacing the literal contradiction with the actual join key lets the join match rows normally."),
        ]);
}
