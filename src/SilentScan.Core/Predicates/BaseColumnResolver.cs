using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

public sealed class TableColumnKeyComparer(StringComparer identifierComparer) :
    IEqualityComparer<(string Table, string Column)>,
    IEqualityComparer<ColumnProvenance.BaseColumn>
{
    public static readonly TableColumnKeyComparer Instance = new(StringComparer.OrdinalIgnoreCase);

    public static TableColumnKeyComparer For(Catalog.DatabaseCatalog catalog) => new(catalog.IdentifierComparer);

    public bool Equals((string Table, string Column) x, (string Table, string Column) y) =>
        identifierComparer.Equals(x.Table, y.Table)
        && identifierComparer.Equals(x.Column, y.Column);

    public int GetHashCode((string Table, string Column) obj) =>
        HashCode.Combine(identifierComparer.GetHashCode(obj.Table), identifierComparer.GetHashCode(obj.Column));

    public bool Equals(ColumnProvenance.BaseColumn? x, ColumnProvenance.BaseColumn? y) =>
        x is null || y is null
            ? ReferenceEquals(x, y)
            : identifierComparer.Equals(x.TableQualifiedName, y.TableQualifiedName)
                && identifierComparer.Equals(x.ColumnName, y.ColumnName);

    public int GetHashCode(ColumnProvenance.BaseColumn obj) =>
        HashCode.Combine(identifierComparer.GetHashCode(obj.TableQualifiedName), identifierComparer.GetHashCode(obj.ColumnName));
}

internal static class BaseColumnResolver
{
    public static ColumnProvenance.BaseColumn? ResolveBaseColumn(
        ScalarExpression expression, string sourcePath,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        DatabaseCatalog? catalog = null)
    {
        if (expression is not ColumnReferenceExpression columnRef)
        {
            return null;
        }

        var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null, catalog);
        return provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn
            ? baseColumn
            : null;
    }

    public static IEnumerable<ColumnProvenance.BaseColumn> ResolveBothSides(
        BooleanComparisonExpression predicate, string sourcePath,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        DatabaseCatalog? catalog = null)
    {
        foreach (var side in new[] { predicate.FirstExpression, predicate.SecondExpression })
        {
            if (ResolveBaseColumn(side, sourcePath, scopeChain, catalog) is { } resolved)
            {
                yield return resolved;
            }
        }
    }

    public sealed class ColumnReferenceCollector(
        string sourcePath,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        HashSet<(string Table, string Column)> sink,
        DatabaseCatalog? catalog = null) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {

            if (node.ColumnType != ColumnType.Wildcard)
            {
                var provenance = ScalarExpressionResolver.ResolveColumnReference(node, scopeChain, sourcePath, ledger: null, catalog);
                if (provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
                {
                    sink.Add((baseColumn.TableQualifiedName, baseColumn.ColumnName));
                }
            }

            base.ExplicitVisit(node);
        }
    }
}
