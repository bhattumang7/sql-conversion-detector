using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Folds a comparison between two literal-only expressions (optionally with one level of
/// literal <c>+ - * /</c> arithmetic on either side) to a definite truth value, or a literal
/// arithmetic expression to its numeric value - shared by <see cref="DuplicationScanner"/> (its
/// "always true/false literal comparison" check) and <see cref="TypedPredicateExtractor"/> (to
/// give a foldable tautology/contradiction its own distinct no-column-operand ledger entry
/// instead of an undifferentiated one). Mirrors <c>TypeInference.ExpressionTypeInferencer</c>'s
/// shape: stateless, pure, called ad hoc per-expression.
///
/// Deliberately narrow: non-composed (no AND/OR propagation), literals only (no column/variable
/// folding), and NULL-excluded outright - NULL comparison semantics are their own hazard (three-
/// valued logic) and folding them here would be a different, riskier claim than "these two
/// literals are provably equal/unequal".
/// </summary>
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

    /// <summary>
    /// A numeric literal as-is, or one level of literal <c>+ - * /</c> folded to its value.
    /// Division by zero is never folded - that is a real runtime error in T-SQL, not a value
    /// this can assert a truth about.
    /// </summary>
    public static decimal? TryFoldToNumeric(ScalarExpression expression) => expression switch
    {
        NullLiteral => null, // Never fold NULL - see this type's own doc comment.
        IntegerLiteral integer when decimal.TryParse(integer.Value, out var value) => value,
        NumericLiteral numeric when decimal.TryParse(numeric.Value, out var value) => value,
        BinaryExpression binary => TryFoldArithmetic(binary),
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

    /// <summary>Only a byte-identical (case-sensitive, ordinal) textual match/mismatch is
    /// collation-proof - two textually DIFFERENT string literals are declined entirely for both
    /// '=' and '&lt;&gt;', since a case-insensitive collation could still make them compare equal
    /// at runtime. Never guess.</summary>
    private static bool? EvaluateExactStringMatch(BooleanComparisonType op, StringLiteral first, StringLiteral second)
    {
        if (!string.Equals(first.Value, second.Value, StringComparison.Ordinal))
        {
            return null;
        }

        return op == BooleanComparisonType.Equals;
    }
}
