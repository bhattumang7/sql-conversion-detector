using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Rules;

/// <summary>
/// Roadmap Phase E1: classifies an INSERT/UPDATE assignment's source-vs-target type pair into a
/// <see cref="Predicates.WriteLossKind"/>, or null when the assignment is safe (or not a shape
/// this class knows how to reason about at all - never a guess). Every rule here checks for
/// silent behavior only (see <see cref="Predicates.WriteLossKind"/>'s own doc comment) - a case
/// T-SQL raises a hard error for (too-long VARCHAR, integer overflow) is out of scope by design.
///
/// A literal source expression is inspected for its OWN actual content wherever that is cheap
/// and unambiguous (an all-ASCII string literal into a narrower charset, a whole-number literal
/// into an integer column, a fractional-digit count within the target's scale, a date-only
/// string literal into DATE) - CLAUDE.md's own "never guess" rule cuts both ways: refusing to
/// flag a literal that is PROVABLY safe matters exactly as much as refusing to claim ScanForced
/// without oracle evidence. A non-literal source (column/variable/expression) is always data-
/// dependent, so it is always flagged when the type pair itself is risky - mirroring how
/// <see cref="VerdictClassifier"/> reports what a predicate MAKES POSSIBLE for the engine, not
/// what one specific row's data happens to do.
/// </summary>
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

    // Truncation toward zero, oracle-verified for both a genuinely approximate source
    // (REAL/FLOAT) and a NumericLiteral like `7.9` - which itself types as DECIMAL(2,1), not
    // FLOAT (LiteralTypeResolver: only scientific notation types as float) - into any exact
    // integer target. IsWithinScaleLiteral(literal, 0) is "is this literal a whole number".
    private static bool IsApproximateTruncationRisk(SqlType target, SqlType source, Literal? literal) =>
        IsApproximateNumeric(source.Category) && IsExactIntegerCategory(target.Category) && !IsWithinScaleLiteral(literal, 0);

    private static bool IsNumericScaleNarrowingRisk(SqlType target, SqlType source, Literal? literal)
    {
        if (source.Category != SqlTypeCategory.Decimal || (target.Category != SqlTypeCategory.Decimal && !IsExactIntegerCategory(target.Category)))
        {
            return false;
        }

        // An exact-integer target has no DECIMAL facets of its own - treated as scale 0, exactly
        // like the ApproximateToExactTruncation rule above but for a source that is itself
        // already exact (still silently truncated toward zero, oracle-verified).
        var targetScale = target.Category == SqlTypeCategory.Decimal ? target.Scale ?? 0 : 0;
        var sourceScale = source.Scale ?? 0;
        return targetScale < sourceScale && !IsWithinScaleLiteral(literal, targetScale);
    }

    // A DATE/DATETIME/... source column resolves to a genuine temporal SqlType, but a string
    // literal used in a temporal context stays VARCHAR-typed by this tool's own convention
    // (LiteralTypeResolver: "date literals stay strings until compared") - so the source-side
    // check has to accept a string family too, not just a widertemporal one, or every literal
    // DATE/DATETIME assignment would be invisible to this rule entirely.
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

    /// <summary>True only when every character of a string literal's own content is ASCII - safe under ANY single-byte non-Unicode codepage, so flagging it would be a guess this class refuses to make. A non-literal (null) source is never provably safe.</summary>
    private static bool IsAsciiOnlyLiteral(Literal? literal) =>
        literal is StringLiteral stringLiteral && stringLiteral.Value.All(c => c <= 127);

    /// <summary>
    /// True when a numeric literal's own value needs at most <paramref name="targetScale"/>
    /// decimal digits - not merely when it happens to be WRITTEN with that few (a trailing-zero
    /// literal like <c>7.0</c> against targetScale 0, or <c>123.400</c> against targetScale 2,
    /// is exactly as safe as one written more tersely; only a non-zero digit past the target's
    /// scale is a real loss). <paramref name="targetScale"/> 0 is "is this a whole number".
    /// </summary>
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

        // A non-numeric literal (a string/date literal, or null - not a literal at all) or
        // scientific notation is not worth the parsing complexity to prove safe either way, so
        // treated as "can't tell", which for this literal-safety check means not provably within
        // scale.
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

    /// <summary>Lexical, not a full date parse (T-SQL's own literal-format grammar is broader than any single .NET parser) - a literal with neither a time separator is treated as date-only, everything else (including one this can't read at all) is treated as not provably safe.</summary>
    private static bool IsDateOnlyLiteral(Literal? literal) =>
        literal is StringLiteral stringLiteral
        && !stringLiteral.Value.Contains(':', StringComparison.Ordinal)
        && !stringLiteral.Value.Contains('T', StringComparison.OrdinalIgnoreCase);
}
