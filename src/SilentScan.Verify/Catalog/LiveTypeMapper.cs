using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Catalog;

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

        _ => null,
    };

    private static bool IsStringOrBinaryFamily(SqlTypeCategory category) => category is
        SqlTypeCategory.Char or SqlTypeCategory.VarChar or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar
        or SqlTypeCategory.Binary or SqlTypeCategory.VarBinary;

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

        if (category is SqlTypeCategory.Time or SqlTypeCategory.DateTime2 or SqlTypeCategory.DateTimeOffset)
        {
            return new SqlType(category.Value, Scale: scale);
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
