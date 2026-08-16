using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Parsing;

/// <summary>Resolves a ScriptDOM <see cref="DataTypeReference"/> (as written in DDL) to a <see cref="SqlType"/>.</summary>
public static class SqlTypeReferenceResolver
{
    private const string SysnameTypeName = "sysname";

    /// <param name="dataType">The type as written in DDL.</param>
    /// <param name="columnCollation">The COLUMN's own COLLATE clause, if any is present on the declaration.</param>
    /// <param name="typeAliases">
    /// CREATE TYPE ... FROM aliases discovered elsewhere in the scan (<see
    /// cref="Catalog.DatabaseCatalog.TypeAliases"/>), keyed by qualified name - resolves
    /// <paramref name="dataType"/> through to its underlying built-in type when it references
    /// one (docs/audit-remediation-plan.md Phase 6.2). Null when the caller has no catalog
    /// available at this point in the pipeline (an alias reference there still resolves via the
    /// sysname special-case below, just not via a user-declared alias).
    /// </param>
    public static SqlType? Resolve(DataTypeReference dataType, Identifier? columnCollation, IReadOnlyDictionary<string, SqlType>? typeAliases = null)
    {
        if (dataType is UserDataTypeReference userType)
        {
            return ResolveUserType(userType, columnCollation, typeAliases);
        }

        if (dataType is not SqlDataTypeReference sqlDataType)
        {
            // Table types (ColumnType) and CLR UDTs (assembly-backed types) are out of scope
            // for v1's type-precedence reasoning; callers should treat this as
            // SqlTypeCategory.UserDefined.
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
            _ when IsStringOrBinaryFamily(category.Value) => ResolveStringOrBinary(category.Value, sqlDataType, collation),
            _ when IsFractionalSecondsFamily(category.Value) => ResolveFractionalSeconds(category.Value, sqlDataType),
            _ => new SqlType(category.Value),
        };
    }

    private static bool IsFractionalSecondsFamily(SqlTypeCategory category) => category is
        SqlTypeCategory.Time or SqlTypeCategory.DateTime2 or SqlTypeCategory.DateTimeOffset;

    /// <summary>
    /// TIME/DATETIME2/DATETIMEOFFSET's own <c>(n)</c> fractional-seconds-precision parameter -
    /// previously unresolved entirely (fell into the generic <c>new SqlType(category)</c> branch,
    /// permanently losing the declared scale). Needed for the BETWEEN end-of-period boundary
    /// finding (docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability
    /// stream"): a boundary literal with fewer fractional digits than the column's own declared
    /// scale silently drops rows in the gap, oracle-confirmed. Defaults to 7 when no explicit
    /// <c>(n)</c> is given - T-SQL's own default precision for all three types.
    /// </summary>
    private static SqlType ResolveFractionalSeconds(SqlTypeCategory category, SqlDataTypeReference sqlDataType)
    {
        var scale = sqlDataType.Parameters.Count > 0 && sqlDataType.Parameters[0] is IntegerLiteral s
            ? int.Parse(s.Value, CultureInfo.InvariantCulture)
            : 7;

        return new SqlType(category, Scale: scale);
    }

    private static SqlType? ResolveUserType(UserDataTypeReference userType, Identifier? columnCollation, IReadOnlyDictionary<string, SqlType>? typeAliases)
    {
        // sysname is a system-provided alias for nvarchar(128) (SQL Server always parses it as
        // a UserDataTypeReference, never a built-in SqlDataTypeOption - verified directly
        // against the parser) - pervasive in admin-script repos for object/schema names, so
        // this is worth a direct special-case rather than depending on the catalog knowing
        // about it (docs/audit-remediation-plan.md Phase 6.2, audit finding "sysname...
        // pervasive in the admin-script repos this study targets").
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

    /// <summary>
    /// A column/variable declared with a string-family alias can still carry its own explicit
    /// COLLATE clause layered on top (the same as any built-in string type) - the column's own
    /// collation always wins, matching every other collation resolution in this codebase.
    /// </summary>
    private static SqlType ApplyColumnCollation(SqlType type, Identifier? columnCollation) =>
        type.IsStringFamily && columnCollation is { Value.Length: > 0 }
            ? type with { Collation = new Collation(columnCollation.Value) }
            : type;

    private static bool IsStringOrBinaryFamily(SqlTypeCategory category) => category is
        SqlTypeCategory.Char or SqlTypeCategory.VarChar or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar
        or SqlTypeCategory.Binary or SqlTypeCategory.VarBinary;

    private static SqlType ResolveStringOrBinary(SqlTypeCategory category, SqlDataTypeReference sqlDataType, Collation? collation)
    {
        var lengthParam = sqlDataType.Parameters.Count > 0 ? sqlDataType.Parameters[0] : null;
        if (lengthParam is MaxLiteral)
        {
            return new SqlType(category, Collation: collation, IsMax: true);
        }

        var length = lengthParam is IntegerLiteral intLiteral ? int.Parse(intLiteral.Value, CultureInfo.InvariantCulture) : (int?)null;
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
