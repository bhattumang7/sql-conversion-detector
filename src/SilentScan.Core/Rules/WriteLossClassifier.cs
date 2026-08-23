using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public static class WriteLossClassifier
{
    public static Predicates.WriteLossKind? Classify(SqlType? target, SqlType? source, ScalarExpression? sourceExpression)
    {
        if (target is null || source is null)
        {
            return null;
        }

        var literal = Unwrap(sourceExpression) as Literal;

        if (IsUnicodeReplacementRisk(target, source, literal))
        {
            return Predicates.WriteLossKind.UnicodeToNonUnicodeReplacement;
        }

        if (IsApproximateTruncationRisk(target, source, literal))
        {
            return Predicates.WriteLossKind.ApproximateToExactTruncation;
        }

        if (IsNumericScaleNarrowingRisk(target, source, literal))
        {
            return Predicates.WriteLossKind.NumericScaleNarrowing;
        }

        if (IsTemporalPrecisionLossRisk(target, source, literal))
        {
            return Predicates.WriteLossKind.TemporalPrecisionLoss;
        }

        return null;
    }

    private static bool IsUnicodeReplacementRisk(SqlType target, SqlType source, Literal? literal) =>
        source.IsUnicodeString && target.IsNonUnicodeString && !IsAsciiOnlyLiteral(literal);

    private static bool IsApproximateTruncationRisk(SqlType target, SqlType source, Literal? literal) =>
        IsApproximateNumeric(source.Category) && IsExactIntegerCategory(target.Category) && !IsWithinScaleLiteral(literal, 0);

    private static bool IsNumericScaleNarrowingRisk(SqlType target, SqlType source, Literal? literal)
    {
        if (source.Category != SqlTypeCategory.Decimal || (target.Category != SqlTypeCategory.Decimal && !IsExactIntegerCategory(target.Category)))
        {
            return false;
        }

        var targetScale = target.Category == SqlTypeCategory.Decimal ? target.Scale ?? 0 : 0;
        var sourceScale = source.Scale ?? 0;
        return targetScale < sourceScale && !IsWithinScaleLiteral(literal, targetScale);
    }

    private static bool IsTemporalPrecisionLossRisk(SqlType target, SqlType source, Literal? literal) =>
        target.Category == SqlTypeCategory.Date && (IsWiderTemporal(source.Category) || source.IsStringFamily) && !IsDateOnlyLiteral(literal);

    private static ScalarExpression? Unwrap(ScalarExpression? expression) => expression switch
    {
        ParenthesisExpression paren => Unwrap(paren.Expression),
        UnaryExpression unary => Unwrap(unary.Expression),
        _ => expression,
    };

    private static bool IsApproximateNumeric(SqlTypeCategory category) =>
        category is SqlTypeCategory.Real or SqlTypeCategory.Float;

    private static bool IsExactIntegerCategory(SqlTypeCategory category) =>
        category is SqlTypeCategory.TinyInt or SqlTypeCategory.SmallInt or SqlTypeCategory.Int or SqlTypeCategory.BigInt;

    private static bool IsWiderTemporal(SqlTypeCategory category) =>
        category is SqlTypeCategory.DateTime or SqlTypeCategory.DateTime2 or SqlTypeCategory.SmallDateTime or SqlTypeCategory.DateTimeOffset;

private static bool IsAsciiOnlyLiteral(Literal? literal) =>
        literal is StringLiteral stringLiteral && stringLiteral.Value.All(c => c <= 127);

private static bool IsWithinScaleLiteral(Literal? literal, int targetScale)
    {
        if (literal is IntegerLiteral)
        {
            return true;
        }

        var text = literal switch
        {
            NumericLiteral n => n.Value,
            RealLiteral r => r.Value,
            _ => null,
        };

        if (text is null || text.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return true;
        }

        var fractional = text[(dot + 1)..];
        return fractional.Length <= targetScale || fractional[targetScale..].All(c => c == '0');
    }

private static bool IsDateOnlyLiteral(Literal? literal) =>
        literal is StringLiteral stringLiteral
        && !stringLiteral.Value.Contains(':', StringComparison.Ordinal)
        && !stringLiteral.Value.Contains('T', StringComparison.OrdinalIgnoreCase);
}
