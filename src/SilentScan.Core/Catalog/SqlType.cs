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
    bool IsMax = false)
{
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

    /// <summary>
    /// Oracle-verified against SQL Server 2022: widening within the numeric-or-bit family
    /// (e.g. int vs bigint, bit vs int) or within the date/time family (e.g. date vs
    /// datetime) never produces a CONVERT_IMPLICIT in the plan, regardless of which side's
    /// precedence is lower - unlike crossing into the string family, which always does.
    /// See VerdictClassifier for the specific probes this claim rests on.
    /// </summary>
    public bool IsWideningCompatibleWith(SqlType other) =>
        (IsNumericOrBit && other.IsNumericOrBit) || (IsDateTimeFamily && other.IsDateTimeFamily);

    private bool IsNumericOrBit => IsNumericFamily || Category == SqlTypeCategory.Bit;

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
