using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Parsing;

/// <summary>Maps ScriptDOM's <see cref="SqlDataTypeOption"/> to our <see cref="SqlTypeCategory"/>.</summary>
public static class SqlDataTypeMapper
{
    public static SqlTypeCategory? Map(SqlDataTypeOption option) => option switch
    {
        SqlDataTypeOption.BigInt => SqlTypeCategory.BigInt,
        SqlDataTypeOption.Int => SqlTypeCategory.Int,
        SqlDataTypeOption.SmallInt => SqlTypeCategory.SmallInt,
        SqlDataTypeOption.TinyInt => SqlTypeCategory.TinyInt,
        SqlDataTypeOption.Bit => SqlTypeCategory.Bit,
        SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric => SqlTypeCategory.Decimal,
        SqlDataTypeOption.Money => SqlTypeCategory.Money,
        SqlDataTypeOption.SmallMoney => SqlTypeCategory.SmallMoney,
        SqlDataTypeOption.Float => SqlTypeCategory.Float,
        SqlDataTypeOption.Real => SqlTypeCategory.Real,
        SqlDataTypeOption.DateTime => SqlTypeCategory.DateTime,
        SqlDataTypeOption.SmallDateTime => SqlTypeCategory.SmallDateTime,
        SqlDataTypeOption.Char => SqlTypeCategory.Char,
        SqlDataTypeOption.VarChar => SqlTypeCategory.VarChar,
        SqlDataTypeOption.Text => SqlTypeCategory.Text,
        SqlDataTypeOption.NChar => SqlTypeCategory.NChar,
        SqlDataTypeOption.NVarChar => SqlTypeCategory.NVarChar,
        SqlDataTypeOption.NText => SqlTypeCategory.NText,
        SqlDataTypeOption.Binary => SqlTypeCategory.Binary,
        SqlDataTypeOption.VarBinary => SqlTypeCategory.VarBinary,
        SqlDataTypeOption.Image => SqlTypeCategory.Image,
        SqlDataTypeOption.Sql_Variant => SqlTypeCategory.SqlVariant,
        SqlDataTypeOption.Timestamp or SqlDataTypeOption.Rowversion => SqlTypeCategory.Timestamp,
        SqlDataTypeOption.UniqueIdentifier => SqlTypeCategory.UniqueIdentifier,
        SqlDataTypeOption.Date => SqlTypeCategory.Date,
        SqlDataTypeOption.Time => SqlTypeCategory.Time,
        SqlDataTypeOption.DateTime2 => SqlTypeCategory.DateTime2,
        SqlDataTypeOption.DateTimeOffset => SqlTypeCategory.DateTimeOffset,
        SqlDataTypeOption.Json => SqlTypeCategory.Json,

        // Cursor/Table/Vector/None are not scalar comparison types this tool reasons about.
        _ => null,
    };
}
