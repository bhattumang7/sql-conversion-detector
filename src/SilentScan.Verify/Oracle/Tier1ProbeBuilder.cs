using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Verify.Oracle;

public static class Tier1ProbeBuilder
{
    public static string? Build(SargabilityFinding finding, DatabaseCatalog catalog)
    {
        if (finding.PredicateFragmentText is not { } fragmentText || finding.TableQualifiedName is not { } tableQualifiedName)
        {
            return null;
        }

        var table = BracketQualifiedName(tableQualifiedName);

        if (finding.Kind is SargabilityFindingKind.LeadingWildcardLike or SargabilityFindingKind.LikePatternNotLiteral)
        {

            return $"SELECT 1 FROM {table} WHERE {fragmentText};";
        }

        var columnType = catalog.Find(tableQualifiedName)?.FindColumn(finding.ColumnName)?.Type;
        if (columnType is null)
        {
            return null;
        }

        var typeSyntax = SqlTypeSyntaxFormatter.Format(columnType);
        if (typeSyntax is null)
        {
            return null;
        }

        var collateClause = SqlTypeSyntaxFormatter.FormatCollateClause(columnType);
        return $"""
            DECLARE @p {typeSyntax};
            SELECT 1 FROM {table} WHERE {fragmentText} = @p{collateClause};
            """;
    }

    private static string BracketQualifiedName(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
