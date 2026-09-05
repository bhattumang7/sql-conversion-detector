namespace SilentScan.Core.TypeInference;

public sealed record SqlType(
    SqlTypeCategory Category,
    int? Length = null,
    int? Precision = null,
    int? Scale = null,
    Collation? Collation = null,
    bool IsMax = false,
    bool LengthKnown = true)
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

    public bool IsBinaryFamily => Category is SqlTypeCategory.Binary or SqlTypeCategory.VarBinary;

    public bool IsLegacyLob => Category is SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image;

    public bool IsLegalLegacyLobConversionTarget =>
        !IsLegacyLob || Collation is not { } collation || (!collation.IsUtf8 && !collation.IsSupplementaryCharacterAware);

    public bool NeedsConversionFrom(SqlType source) =>
        Category != source.Category
        || (IsStringFamily && Collation is { } targetCollation && source.Collation is { } sourceCollation
            && !string.Equals(targetCollation.Name, sourceCollation.Name, StringComparison.OrdinalIgnoreCase));

    public bool IsFractionalSecondsFamily => Category is SqlTypeCategory.Time
        or SqlTypeCategory.DateTime2 or SqlTypeCategory.DateTimeOffset;

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
