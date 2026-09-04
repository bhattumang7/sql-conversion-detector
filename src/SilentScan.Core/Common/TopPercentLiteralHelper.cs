using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Common;

internal static class TopPercentLiteralHelper
{
    public static bool IsHundredPercentLiteral(ScalarExpression expression) =>
        Unwrap(expression) switch
        {
            IntegerLiteral { Value: "100" } => true,
            NumericLiteral { Value: var value } => IsExactlyOneHundred(value),
            _ => false,
        };

    private static bool IsExactlyOneHundred(string value)
    {
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot < 0 ? value : value[..dot];
        var fractionalPart = dot < 0 ? string.Empty : value[(dot + 1)..];
        return integerPart == "100" && fractionalPart.All(c => c == '0');
    }

    private static ScalarExpression Unwrap(ScalarExpression expression) =>
        expression is ParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;
}
