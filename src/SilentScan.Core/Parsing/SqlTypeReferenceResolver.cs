using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Core.Parsing;

public static class SqlTypeReferenceResolver
{
    private const string SysnameTypeName = "sysname";

public static SqlType? Resolve(
        DataTypeReference dataType, Identifier? columnCollation, IReadOnlyDictionary<string, SqlType>? typeAliases = null,
        int? unsizedStringOrBinaryDefaultLength = null)
    {
        if (dataType is UserDataTypeReference userType)
        {
            return ResolveUserType(userType, columnCollation, typeAliases);
        }

        if (dataType is XmlDataTypeReference)
        {
            return new SqlType(SqlTypeCategory.Xml);
        }

        if (dataType is not SqlDataTypeReference sqlDataType)
        {
            return null;
        }

        var category = SqlDataTypeMapper.Map(sqlDataType.SqlDataTypeOption);
        if (category is null)
        {
            return null;
        }

        var collation = columnCollation is { Value.Length: > 0 } ? new Collation(columnCollation.Value) : null;

        return sqlDataType.SqlDataTypeOption switch
        {
            SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric => ResolveDecimal(category.Value, sqlDataType),
            _ when IsStringOrBinaryFamily(category.Value) => ResolveStringOrBinary(category.Value, sqlDataType, collation, unsizedStringOrBinaryDefaultLength),
            _ when IsFractionalSecondsFamily(category.Value) => ResolveFractionalSeconds(category.Value, sqlDataType),
            _ => new SqlType(category.Value),
        };
    }

    private static bool IsFractionalSecondsFamily(SqlTypeCategory category) => category is
        SqlTypeCategory.Time or SqlTypeCategory.DateTime2 or SqlTypeCategory.DateTimeOffset;

private static SqlType ResolveFractionalSeconds(SqlTypeCategory category, SqlDataTypeReference sqlDataType)
    {
        var scale = sqlDataType.Parameters.Count > 0 && sqlDataType.Parameters[0] is IntegerLiteral s
            ? int.Parse(s.Value, CultureInfo.InvariantCulture)
            : 7;

        return new SqlType(category, Scale: scale);
    }

    private static SqlType? ResolveUserType(UserDataTypeReference userType, Identifier? columnCollation, IReadOnlyDictionary<string, SqlType>? typeAliases)
    {
        if (string.Equals(userType.Name.BaseIdentifier.Value, SysnameTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return ApplyColumnCollation(new SqlType(SqlTypeCategory.NVarChar, Length: 128), columnCollation);
        }

        if (typeAliases is null)
        {
            return null;
        }

        var qualifiedName = SchemaObjectNameHelper.Qualify(userType.Name);
        return typeAliases.TryGetValue(qualifiedName, out var aliasedType)
            ? ApplyColumnCollation(aliasedType, columnCollation)
            : null;
    }

private static SqlType ApplyColumnCollation(SqlType type, Identifier? columnCollation) =>
        type.IsStringFamily && columnCollation is { Value.Length: > 0 }
            ? type with { Collation = new Collation(columnCollation.Value) }
            : type;

    private static bool IsStringOrBinaryFamily(SqlTypeCategory category) => category is
        SqlTypeCategory.Char or SqlTypeCategory.VarChar or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar
        or SqlTypeCategory.Binary or SqlTypeCategory.VarBinary;

    private static SqlType ResolveStringOrBinary(
        SqlTypeCategory category, SqlDataTypeReference sqlDataType, Collation? collation, int? unsizedDefaultLength)
    {
        var lengthParam = sqlDataType.Parameters.Count > 0 ? sqlDataType.Parameters[0] : null;
        if (lengthParam is MaxLiteral)
        {
            return new SqlType(category, Collation: collation, IsMax: true);
        }

        var length = lengthParam is IntegerLiteral intLiteral
            ? int.Parse(intLiteral.Value, CultureInfo.InvariantCulture)
            : unsizedDefaultLength;
        return new SqlType(category, Length: length, Collation: collation);
    }

    private static SqlType ResolveDecimal(SqlTypeCategory category, SqlDataTypeReference sqlDataType)
    {
        int? precision = null;
        int? scale = null;
        if (sqlDataType.Parameters.Count > 0 && sqlDataType.Parameters[0] is IntegerLiteral p)
        {
            precision = int.Parse(p.Value, CultureInfo.InvariantCulture);
        }

        if (sqlDataType.Parameters.Count > 1 && sqlDataType.Parameters[1] is IntegerLiteral s)
        {
            scale = int.Parse(s.Value, CultureInfo.InvariantCulture);
        }

        return new SqlType(category, Precision: precision, Scale: scale);
    }
}
