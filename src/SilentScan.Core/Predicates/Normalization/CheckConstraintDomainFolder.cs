using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates.Normalization;

internal static class CheckConstraintDomainFolder
{
    private enum CmpOp { Eq, Ne, Lt, Le, Gt, Ge }

    public static NumericValueRangeSet? TryBuildRangeSet(BooleanExpression node, string columnName, StringComparer comparer) => node switch
    {
        BooleanParenthesisExpression paren => TryBuildRangeSet(paren.Expression, columnName, comparer),

        BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and =>
            Combine(TryBuildRangeSet(and.FirstExpression, columnName, comparer), TryBuildRangeSet(and.SecondExpression, columnName, comparer), static (a, b) => a.Intersect(b)),

        BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } or_ =>
            Combine(TryBuildRangeSet(or_.FirstExpression, columnName, comparer), TryBuildRangeSet(or_.SecondExpression, columnName, comparer), static (a, b) => a.Union(b)),

        BooleanTernaryExpression { TernaryExpressionType: BooleanTernaryExpressionType.Between } between => TryBetweenRangeSet(between, columnName, comparer),

        BooleanComparisonExpression cmp => TryFoldLeaf(cmp, columnName, comparer),

        _ => null,
    };

    public static NumericValueRangeSet? TryBetweenRangeSet(BooleanTernaryExpression between, string columnName, StringComparer comparer)
    {
        if (!IsColumnReference(between.FirstExpression, columnName, comparer))
        {
            return null;
        }

        if (TryGetNumericLiteral(between.SecondExpression) is not { } lower || TryGetNumericLiteral(between.ThirdExpression) is not { } upper)
        {
            return null;
        }

        return NumericValueRangeSet.ForGreaterThanOrEqual(lower).Intersect(NumericValueRangeSet.ForLessThanOrEqual(upper));
    }

    private static NumericValueRangeSet? Combine(NumericValueRangeSet? left, NumericValueRangeSet? right, Func<NumericValueRangeSet, NumericValueRangeSet, NumericValueRangeSet> combine) =>
        left is null || right is null ? null : combine(left, right);

    private static NumericValueRangeSet? TryFoldLeaf(BooleanComparisonExpression cmp, string columnName, StringComparer comparer)
    {
        if (IsColumnReference(cmp.FirstExpression, columnName, comparer))
        {
            return TryRangeSet(cmp.ComparisonType, cmp.SecondExpression, literalOnRight: true);
        }

        if (IsColumnReference(cmp.SecondExpression, columnName, comparer))
        {
            return TryRangeSet(cmp.ComparisonType, cmp.FirstExpression, literalOnRight: false);
        }

        return null;
    }

    public static NumericValueRangeSet? TryRangeSet(BooleanComparisonType comparisonType, ScalarExpression literalExpression, bool literalOnRight)
    {
        var op = ToCmpOp(comparisonType);
        if (op is null)
        {
            return null;
        }

        var literal = TryGetNumericLiteral(literalExpression);
        if (literal is null)
        {
            return null;
        }

        var effectiveOp = literalOnRight ? op.Value : Flip(op.Value);
        return ToRangeSet(effectiveOp, literal.Value);
    }

    public static bool IsColumnReference(ScalarExpression expression, string columnName, StringComparer comparer)
    {
        while (expression is ParenthesisExpression parenthesis)
        {
            expression = parenthesis.Expression;
        }

        return expression is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } identifiers }
            && comparer.Equals(identifiers[^1].Value, columnName);
    }

    public static decimal? TryGetNumericLiteral(ScalarExpression expression)
    {
        while (expression is ParenthesisExpression parenthesis)
        {
            expression = parenthesis.Expression;
        }

        return expression switch
        {
            IntegerLiteral lit => ParseDecimal(lit.Value),
            NumericLiteral lit => ParseDecimal(lit.Value),
            MoneyLiteral lit => ParseDecimal(lit.Value),

            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary =>
                TryGetNumericLiteral(unary.Expression) is { } v ? -v : null,
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary =>
                TryGetNumericLiteral(unary.Expression),
            _ => null,
        };
    }

    private static decimal? ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static CmpOp? ToCmpOp(BooleanComparisonType type) => type switch
    {
        BooleanComparisonType.Equals => CmpOp.Eq,
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => CmpOp.Ne,
        BooleanComparisonType.LessThan => CmpOp.Lt,
        BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.NotGreaterThan => CmpOp.Le,
        BooleanComparisonType.GreaterThan => CmpOp.Gt,
        BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.NotLessThan => CmpOp.Ge,
        _ => null,
    };

    private static CmpOp Flip(CmpOp op) => op switch
    {
        CmpOp.Lt => CmpOp.Gt,
        CmpOp.Gt => CmpOp.Lt,
        CmpOp.Le => CmpOp.Ge,
        CmpOp.Ge => CmpOp.Le,
        _ => op,
    };

    private static NumericValueRangeSet ToRangeSet(CmpOp op, decimal value) => op switch
    {
        CmpOp.Eq => NumericValueRangeSet.ForEquals(value),
        CmpOp.Ne => NumericValueRangeSet.ForNotEquals(value),
        CmpOp.Lt => NumericValueRangeSet.ForLessThan(value),
        CmpOp.Le => NumericValueRangeSet.ForLessThanOrEqual(value),
        CmpOp.Gt => NumericValueRangeSet.ForGreaterThan(value),
        CmpOp.Ge => NumericValueRangeSet.ForGreaterThanOrEqual(value),
        _ => NumericValueRangeSet.Universal,
    };
}
