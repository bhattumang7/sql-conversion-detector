using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

public sealed class TableColumnKeyComparer :
    IEqualityComparer<(string Table, string Column)>,
    IEqualityComparer<ColumnProvenance.BaseColumn>
{
    public static readonly TableColumnKeyComparer Instance = new();

    public bool Equals((string Table, string Column) x, (string Table, string Column) y) =>
        string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.Column, y.Column, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Table, string Column) obj) =>
        HashCode.Combine(obj.Table.ToUpperInvariant(), obj.Column.ToUpperInvariant());

    public bool Equals(ColumnProvenance.BaseColumn? x, ColumnProvenance.BaseColumn? y) =>
        x is null || y is null
            ? ReferenceEquals(x, y)
            : string.Equals(x.TableQualifiedName, y.TableQualifiedName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ColumnName, y.ColumnName, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(ColumnProvenance.BaseColumn obj) =>
        HashCode.Combine(obj.TableQualifiedName.ToUpperInvariant(), obj.ColumnName.ToUpperInvariant());
}

internal static class BaseColumnResolver
{
    public static ColumnProvenance.BaseColumn? ResolveBaseColumn(
        ScalarExpression expression, string sourcePath,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
    {
        if (expression is not ColumnReferenceExpression columnRef)
        {
            return null;
        }

        var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
        return provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn
            ? baseColumn
            : null;
    }

    public static IEnumerable<ColumnProvenance.BaseColumn> ResolveBothSides(
        BooleanComparisonExpression predicate, string sourcePath,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
    {
        foreach (var side in new[] { predicate.FirstExpression, predicate.SecondExpression })
        {
            if (ResolveBaseColumn(side, sourcePath, scopeChain) is { } resolved)
            {
                yield return resolved;
            }
        }
    }

    public sealed class ColumnReferenceCollector(
        string sourcePath,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        HashSet<(string Table, string Column)> sink) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {

            if (node.ColumnType != ColumnType.Wildcard)
            {
                var provenance = ScalarExpressionResolver.ResolveColumnReference(node, scopeChain, sourcePath, ledger: null);
                if (provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
                {
                    sink.Add((baseColumn.TableQualifiedName, baseColumn.ColumnName));
                }
            }

            base.ExplicitVisit(node);
        }
    }
}
