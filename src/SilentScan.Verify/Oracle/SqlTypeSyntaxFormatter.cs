using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public static class SqlTypeSyntaxFormatter
{
    public static string? Format(SqlType type)
    {
        var keyword = CategoryKeyword(type.Category);
        if (keyword is null)
        {
            return null;
        }

        return $"{keyword}{FormatFacet(type)}";
    }

    public static string FormatCollateClause(SqlType type) =>
        type.Collation is { } c ? $" COLLATE {c.Name}" : string.Empty;

    private static string FormatFacet(SqlType type) => type.Category switch
    {
        SqlTypeCategory.Char or SqlTypeCategory.NChar => $"({type.Length ?? 1})",
        SqlTypeCategory.VarChar or SqlTypeCategory.NVarChar or SqlTypeCategory.VarBinary
            => type.IsMax ? "(MAX)" : $"({type.Length ?? 4000})",
        SqlTypeCategory.Binary => $"({type.Length ?? 1})",
        SqlTypeCategory.Decimal => $"({type.Precision ?? 18},{type.Scale ?? 0})",
        _ => string.Empty,
    };

    private static string? CategoryKeyword(SqlTypeCategory category) => category switch
    {
        SqlTypeCategory.SqlVariant => "SQL_VARIANT",
        SqlTypeCategory.UserDefined => null,
        _ => category.ToString().ToUpperInvariant(),
    };
}
