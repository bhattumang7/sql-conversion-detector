namespace SilentScan.Core.Catalog;

/// <summary>
/// A fully-resolved T-SQL scalar type: category plus the facets that affect
/// comparison semantics (length/precision/scale, and collation for string types).
/// </summary>
public sealed record SqlType(
    SqlTypeCategory Category,
    int? Length = null,
    int? Precision = null,
    int? Scale = null,
    Collation? Collation = null,
    bool IsMax = false,
    bool LengthKnown = true)
{
    // LengthKnown distinguishes "Length is null because this facet genuinely has none, or was
    // genuinely declared with none" (the default, true) from "Length is null because this pass
    // couldn't compute it" (e.g. ExpressionTypeInferencer.Combine's cross-category merge, which
    // has no way to know a merged result's true length) - a caller that reads a null Length as
    // "declared with T-SQL's implicit length-1 default" must check this first, or it fabricates a
    // cause for a length it never actually inferred. See Rules.ParameterLengthClassifier.

    public bool IsStringFamily => Category is SqlTypeCategory.Char or SqlTypeCategory.VarChar
        or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar or SqlTypeCategory.Text
        or SqlTypeCategory.NText;

    public bool IsUnicodeString => Category is SqlTypeCategory.NChar or SqlTypeCategory.NVarChar
        or SqlTypeCategory.NText;

    public bool IsNonUnicodeString => Category is SqlTypeCategory.Char or SqlTypeCategory.VarChar
        or SqlTypeCategory.Text;

    public bool IsNumericFamily => Category is SqlTypeCategory.TinyInt or SqlTypeCategory.SmallInt
        or SqlTypeCategory.Int or SqlTypeCategory.BigInt or SqlTypeCategory.SmallMoney
        or SqlTypeCategory.Money or SqlTypeCategory.Decimal or SqlTypeCategory.Real or SqlTypeCategory.Float;

    public bool IsDateTimeFamily => Category is SqlTypeCategory.Date or SqlTypeCategory.Time
        or SqlTypeCategory.SmallDateTime or SqlTypeCategory.DateTime or SqlTypeCategory.DateTime2
        or SqlTypeCategory.DateTimeOffset;

    public override string ToString()
    {
        var baseName = Category.ToString();
        var facet = FormatFacet();
        var collationSuffix = Collation is { } c ? $" COLLATE {c.Name}" : string.Empty;
        return $"{baseName}{facet}{collationSuffix}";
    }

    private string FormatFacet()
    {
        if (IsMax)
        {
            return "(max)";
        }

        if (Length is { } len)
        {
            return $"({len})";
        }

        if (Precision is { } p)
        {
            return Scale is { } s ? $"({p},{s})" : $"({p})";
        }

        return string.Empty;
    }
}
