using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Renders a resolved <see cref="SqlType"/> as valid T-SQL type syntax for a DECLARE
/// statement, so the corpus oracle can synthesize a parameter of the exact type a finding's
/// non-column operand was inferred to have (CLAUDE.md Verify: probe the real column against
/// a representative value of the other side's type). Facet values (length/precision/scale)
/// don't affect conversion behavior - only the type category does - so missing facets fall
/// back to permissive defaults rather than making the finding unprobeable.
/// </summary>
public static class SqlTypeSyntaxFormatter
{
    /// <summary>
    /// Returns the DECLARE-able syntax for <paramref name="type"/> (never including a COLLATE
    /// clause - T-SQL rejects COLLATE on a variable declaration outright, verified against the
    /// Docker oracle; see <see cref="FormatCollateClause"/> for the expression-position form),
    /// or null if the category has no fixed T-SQL spelling (e.g. a user-defined type we can't
    /// safely synthesize).
    /// </summary>
    public static string? Format(SqlType type)
    {
        var keyword = CategoryKeyword(type.Category);
        if (keyword is null)
        {
            return null;
        }

        return $"{keyword}{FormatFacet(type)}";
    }

    /// <summary>
    /// Returns " COLLATE &lt;name&gt;" for a type with a resolved collation, or an empty string
    /// otherwise - apply this to the operand's use site in an expression (e.g. `@p COLLATE
    /// ...`), never to its DECLARE statement.
    /// </summary>
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
