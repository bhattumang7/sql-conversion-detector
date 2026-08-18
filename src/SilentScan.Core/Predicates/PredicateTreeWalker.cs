using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Shared AST-shape recursions used by multiple scanners to flatten a FROM clause's join tree or
/// a WHERE/ON/HAVING condition's top-level AND-conjuncts before applying a rule to each leaf.
/// Extracted from six near-identical private copies (docs/detection-checklist.md "Engineering
/// debt" - the `Flatten*` family) - pure AST traversal, no catalog/lineage lookups, no rule
/// decisions, so it belongs alongside the scanners it serves rather than under Rules/.
/// </summary>
internal static class PredicateTreeWalker
{
    /// <summary>
    /// Every <see cref="QualifiedJoin"/> node reachable from a FROM-clause table reference,
    /// recursing through nested joins and parenthesized joins, children before the join itself.
    /// An unqualified join (CROSS JOIN, CROSS/OUTER APPLY) is deliberately a recursion dead end -
    /// same as every six original copies this was extracted from - so a <see cref="QualifiedJoin"/>
    /// nested only under an unqualified join is not reached; a plain table/function/derived-table
    /// reference contributes no node either way.
    /// </summary>
    public static IEnumerable<QualifiedJoin> FlattenJoinNodes(TableReference tableReference)
    {
        switch (tableReference)
        {
            case QualifiedJoin join:
                foreach (var t in FlattenJoinNodes(join.FirstTableReference))
                {
                    yield return t;
                }

                foreach (var t in FlattenJoinNodes(join.SecondTableReference))
                {
                    yield return t;
                }

                yield return join;
                break;

            case JoinParenthesisTableReference parenthesis:
                foreach (var t in FlattenJoinNodes(parenthesis.Join))
                {
                    yield return t;
                }

                break;
        }
    }

    /// <summary>
    /// Every top-level AND-connected fragment reachable without crossing an OR - <c>(A AND B) OR
    /// C</c> yields the single fragment <c>(A AND B) OR C</c> unsplit (OR is a different, deliberately
    /// separate concern - see <c>CatchAllPredicateScanner.FlattenOr</c>), while <c>A AND (B AND C)</c>
    /// and <c>A AND B AND C</c> both yield the three leaves <c>A</c>, <c>B</c>, <c>C</c>.
    /// Parentheses are transparently unwrapped at every level. Null yields no fragments.
    /// </summary>
    public static IEnumerable<BooleanExpression> FlattenAnd(BooleanExpression? expression)
    {
        switch (expression)
        {
            case null:
                yield break;

            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                foreach (var e in FlattenAnd(and.FirstExpression))
                {
                    yield return e;
                }

                foreach (var e in FlattenAnd(and.SecondExpression))
                {
                    yield return e;
                }

                break;

            case BooleanParenthesisExpression paren:
                foreach (var e in FlattenAnd(paren.Expression))
                {
                    yield return e;
                }

                break;

            default:
                yield return expression;
                break;
        }
    }
}
