using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Rules;

public static class WriteLossClassifier
{
    public static Predicates.WriteLossKind? Classify(SqlType? target, SqlType? source, ScalarExpression? sourceExpression, bool isVariableTarget)
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

        if (NumericNarrowingKind(target, source, literal) is { } numericKind)
        {
            return numericKind;
        }

        if (IsTemporalPrecisionLossRisk(target, source, literal))
        {
            return Predicates.WriteLossKind.TemporalPrecisionLoss;
        }

        if (IsTemporalOffsetDroppedRisk(target, source))
        {
            return Predicates.WriteLossKind.TemporalOffsetDropped;
        }

        if (IsTemporalScaleNarrowingRisk(target, source))
        {
            return Predicates.WriteLossKind.TemporalScaleNarrowing;
        }

        if (isVariableTarget && IsLengthTruncationRisk(target, source))
        {
            return Predicates.WriteLossKind.LengthTruncation;
        }

        return null;
    }

    private static bool IsUnicodeReplacementRisk(SqlType target, SqlType source, Literal? literal) =>
        source.IsUnicodeString && target.IsNonUnicodeString && !IsAsciiOnlyLiteral(literal);

    private static Predicates.WriteLossKind? NumericNarrowingKind(SqlType target, SqlType source, Literal? literal)
    {
        if (NumericFamilyNarrowing.Classify(target, source) is not { } result)
        {
            return null;
        }

        if (result.TargetIsExact && IsWithinScaleLiteral(literal, result.TargetScale))
        {
            return null;
        }

        return result.Kind == NumericFamilyNarrowing.Kind.ApproximateToExactTruncation
            ? Predicates.WriteLossKind.ApproximateToExactTruncation
            : Predicates.WriteLossKind.NumericScaleNarrowing;
    }

    private static bool IsTemporalPrecisionLossRisk(SqlType target, SqlType source, Literal? literal) =>
        target.Category == SqlTypeCategory.Date && (IsWiderTemporal(source.Category) || source.IsStringFamily) && !IsDateOnlyLiteral(literal);

    private static bool IsTemporalOffsetDroppedRisk(SqlType target, SqlType source) =>
        source.Category == SqlTypeCategory.DateTimeOffset
        && target.Category is SqlTypeCategory.DateTime2 or SqlTypeCategory.DateTime or SqlTypeCategory.SmallDateTime
            or SqlTypeCategory.Date or SqlTypeCategory.Time;

    private static bool IsTemporalScaleNarrowingRisk(SqlType target, SqlType source) =>
        target.IsFractionalSecondsFamily && source.IsFractionalSecondsFamily
        && target.Scale is { } targetScale && source.Scale is { } sourceScale && targetScale < sourceScale;

    private static bool IsLengthTruncationRisk(SqlType target, SqlType source) =>
        (target.IsStringFamily && source.IsStringFamily || target.IsBinaryFamily && source.IsBinaryFamily)
        && !target.IsMax && !source.IsMax
        && target.Length is { } targetLength && source.Length is { } sourceLength && targetLength < sourceLength;

    private static ScalarExpression? Unwrap(ScalarExpression? expression) => expression switch
    {
        ParenthesisExpression paren => Unwrap(paren.Expression),
        UnaryExpression unary => Unwrap(unary.Expression),
        _ => expression,
    };

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
