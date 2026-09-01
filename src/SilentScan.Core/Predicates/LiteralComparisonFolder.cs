using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

public static class LiteralComparisonFolder
{
    public static bool? TryFoldComparison(ScalarExpression first, ScalarExpression second, BooleanComparisonType op)
    {
        if (TryFoldToNumeric(first) is { } a && TryFoldToNumeric(second) is { } b)
        {
            return EvaluateNumericComparison(op, a, b);
        }

        if (first is StringLiteral s1 && second is StringLiteral s2
            && op is BooleanComparisonType.Equals or BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation)
        {
            return EvaluateExactStringMatch(op, s1, s2);
        }

        return null;
    }

    public static string? TryFoldToString(ScalarExpression expression) => expression switch
    {
        StringLiteral literal => literal.Value,
        ParenthesisExpression paren => TryFoldToString(paren.Expression),
        BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary
            when TryFoldToString(binary.FirstExpression) is { } a && TryFoldToString(binary.SecondExpression) is { } b => a + b,
        _ => null,
    };

    public static decimal? TryFoldToNumeric(ScalarExpression expression) => expression switch
    {
        NullLiteral => null,
        IntegerLiteral integer when decimal.TryParse(integer.Value, out var value) => value,
        NumericLiteral numeric when decimal.TryParse(numeric.Value, out var value) => value,
        BinaryExpression binary => TryFoldArithmetic(binary),
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary =>
            TryFoldToNumeric(unary.Expression) is { } negated ? -negated : null,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary =>
            TryFoldToNumeric(unary.Expression),
        _ => null,
    };

    private static decimal? TryFoldArithmetic(BinaryExpression binary)
    {
        if (TryFoldToNumeric(binary.FirstExpression) is not { } a || TryFoldToNumeric(binary.SecondExpression) is not { } b)
        {
            return null;
        }

        return binary.BinaryExpressionType switch
        {
            BinaryExpressionType.Add => a + b,
            BinaryExpressionType.Subtract => a - b,
            BinaryExpressionType.Multiply => a * b,
            BinaryExpressionType.Divide when b != 0 => a / b,
            _ => null,
        };
    }

    private static bool? EvaluateNumericComparison(BooleanComparisonType op, decimal a, decimal b) => op switch
    {
        BooleanComparisonType.Equals => a == b,
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => a != b,
        BooleanComparisonType.GreaterThan => a > b,
        BooleanComparisonType.GreaterThanOrEqualTo => a >= b,
        BooleanComparisonType.LessThan => a < b,
        BooleanComparisonType.LessThanOrEqualTo => a <= b,
        _ => null,
    };

    private static bool? EvaluateExactStringMatch(BooleanComparisonType op, StringLiteral first, StringLiteral second)
    {
        if (!string.Equals(first.Value, second.Value, StringComparison.Ordinal))
        {
            return null;
        }

        return op == BooleanComparisonType.Equals;
    }
}
