using SilentScan.Core.Catalog;

namespace SilentScan.Live.Catalog;

/// <summary>
/// Maps a <c>sys.types.name</c> value (the base type name a live column/parameter/alias
/// resolves to) to <see cref="SqlTypeCategory"/> and assembles the full <see cref="SqlType"/>
/// from the raw facet columns <c>sys.columns</c> exposes - the live-mode counterpart of
/// <c>SilentScan.Core.Parsing.SqlDataTypeMapper</c>/<c>SqlTypeReferenceResolver</c>, which do
/// the same job from parsed DDL syntax instead of catalog metadata.
/// </summary>
public static class LiveTypeMapper
{
    public static SqlTypeCategory? Map(string typeName) => typeName.ToUpperInvariant() switch
    {
        "BIGINT" => SqlTypeCategory.BigInt,
        "INT" => SqlTypeCategory.Int,
        "SMALLINT" => SqlTypeCategory.SmallInt,
        "TINYINT" => SqlTypeCategory.TinyInt,
        "BIT" => SqlTypeCategory.Bit,
        "DECIMAL" or "NUMERIC" => SqlTypeCategory.Decimal,
        "MONEY" => SqlTypeCategory.Money,
        "SMALLMONEY" => SqlTypeCategory.SmallMoney,
        "FLOAT" => SqlTypeCategory.Float,
        "REAL" => SqlTypeCategory.Real,
        "DATETIME" => SqlTypeCategory.DateTime,
        "SMALLDATETIME" => SqlTypeCategory.SmallDateTime,
        "CHAR" => SqlTypeCategory.Char,
        "VARCHAR" => SqlTypeCategory.VarChar,
        "TEXT" => SqlTypeCategory.Text,
        "NCHAR" => SqlTypeCategory.NChar,
        "NVARCHAR" => SqlTypeCategory.NVarChar,
        // sysname is a system-supplied alias for nvarchar(128) - sys.types carries it as its
        // own row (system_type_id shared with nvarchar) rather than resolving it away, so it
        // needs the same special-case DDL-mode's SqlTypeReferenceResolver already has.
        "SYSNAME" => SqlTypeCategory.NVarChar,
        "NTEXT" => SqlTypeCategory.NText,
        "BINARY" => SqlTypeCategory.Binary,
        "VARBINARY" => SqlTypeCategory.VarBinary,
        "IMAGE" => SqlTypeCategory.Image,
        "SQL_VARIANT" => SqlTypeCategory.SqlVariant,
        "TIMESTAMP" or "ROWVERSION" => SqlTypeCategory.Timestamp,
        "UNIQUEIDENTIFIER" => SqlTypeCategory.UniqueIdentifier,
        "DATE" => SqlTypeCategory.Date,
        "TIME" => SqlTypeCategory.Time,
        "DATETIME2" => SqlTypeCategory.DateTime2,
        "DATETIMEOFFSET" => SqlTypeCategory.DateTimeOffset,

        // xml, geography/geometry, hierarchyid, CLR UDTs, cursor/table are not scalar
        // comparison types this tool reasons about - same scope boundary DDL-mode draws.
        _ => null,
    };

    private static bool IsStringOrBinaryFamily(SqlTypeCategory category) => category is
        SqlTypeCategory.Char or SqlTypeCategory.VarChar or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar
        or SqlTypeCategory.Binary or SqlTypeCategory.VarBinary;

    /// <summary>
    /// Builds the full <see cref="SqlType"/> for one column/parameter row. <paramref name="maxLength"/>
    /// is <c>sys.columns.max_length</c> - byte length for binary/non-unicode strings, -1 for
    /// MAX, and (confusingly) still byte length for nchar/nvarchar, so it is halved for those
    /// two categories to get the character length DDL-mode's own <see cref="SqlType.Length"/>
    /// always means. <paramref name="collationName"/> is the column's own resolved collation
    /// (already the effective one - <c>sys.columns.collation_name</c> is never null for a
    /// string-family column, unlike a DDL-mode column with no explicit COLLATE, so live mode
    /// never needs a database-default fallback the way file-mode's collation-sensitivity report
    /// does).
    /// </summary>
    public static SqlType? BuildType(string typeName, short maxLength, byte precision, byte scale, string? collationName)
    {
        var category = Map(typeName);
        if (category is null)
        {
            return null;
        }

        var collation = collationName is { Length: > 0 } ? new Collation(collationName) : null;

        if (category is SqlTypeCategory.Decimal)
        {
            return new SqlType(category.Value, Precision: precision, Scale: scale);
        }

        if (!IsStringOrBinaryFamily(category.Value))
        {
            return new SqlType(category.Value);
        }

        if (maxLength < 0)
        {
            return new SqlType(category.Value, Collation: collation, IsMax: true);
        }

        var isUnicode = category.Value is SqlTypeCategory.NChar or SqlTypeCategory.NVarChar;
        var length = isUnicode ? maxLength / 2 : maxLength;
        return new SqlType(category.Value, Length: length, Collation: collation);
    }
}
