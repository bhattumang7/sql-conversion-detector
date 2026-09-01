using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

internal static class PredicateTreeWalker
{
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

    public static IEnumerable<UnqualifiedJoin> FlattenUnqualifiedJoins(TableReference tableReference)
    {
        switch (tableReference)
        {
            case UnqualifiedJoin join:
                foreach (var t in FlattenUnqualifiedJoins(join.FirstTableReference))
                {
                    yield return t;
                }

                foreach (var t in FlattenUnqualifiedJoins(join.SecondTableReference))
                {
                    yield return t;
                }

                yield return join;
                break;

            case QualifiedJoin qualified:
                foreach (var t in FlattenUnqualifiedJoins(qualified.FirstTableReference))
                {
                    yield return t;
                }

                foreach (var t in FlattenUnqualifiedJoins(qualified.SecondTableReference))
                {
                    yield return t;
                }

                break;

            case JoinParenthesisTableReference parenthesis:
                foreach (var t in FlattenUnqualifiedJoins(parenthesis.Join))
                {
                    yield return t;
                }

                break;
        }
    }

    public static IEnumerable<NamedTableReference> FlattenNamedTables(TableReference tableReference)
    {
        switch (tableReference)
        {
            case NamedTableReference named:
                yield return named;
                break;

            case QualifiedJoin join:
                foreach (var t in FlattenNamedTables(join.FirstTableReference))
                {
                    yield return t;
                }

                foreach (var t in FlattenNamedTables(join.SecondTableReference))
                {
                    yield return t;
                }

                break;

            case UnqualifiedJoin unqualified:
                foreach (var t in FlattenNamedTables(unqualified.FirstTableReference))
                {
                    yield return t;
                }

                foreach (var t in FlattenNamedTables(unqualified.SecondTableReference))
                {
                    yield return t;
                }

                break;

            case JoinParenthesisTableReference parenthesis:
                foreach (var t in FlattenNamedTables(parenthesis.Join))
                {
                    yield return t;
                }

                break;
        }
    }
}
