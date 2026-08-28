using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

internal static class JoinKeyUniqueness
{
    public static List<string> EqualityColumnsQualifiedBy(BooleanExpression? searchCondition, string sourceAlias, StringComparer? identifierComparer = null) =>
        PredicateTreeWalker.FlattenAnd(searchCondition)
            .OfType<BooleanComparisonExpression>()
            .Where(c => c.ComparisonType == BooleanComparisonType.Equals)
            .SelectMany(c => new[] { c.FirstExpression, c.SecondExpression })
            .Select(e => ColumnAliasHelpers.ColumnNameIfQualifiedByAlias(e, sourceAlias, identifierComparer))
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct(identifierComparer ?? StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsProvenUniqueOver(CatalogTable table, IReadOnlyCollection<string> joinColumns, StringComparer? identifierComparer = null)
    {
        var comparer = identifierComparer ?? StringComparer.OrdinalIgnoreCase;
        return table.Indexes.Any(ix =>
            ix.IsUnique && !ix.IsFiltered && !ix.IsDisabled
            && ix.KeyColumns.Count > 0
            && ix.KeyColumns.All(kc => joinColumns.Contains(kc, comparer)));
    }
}
