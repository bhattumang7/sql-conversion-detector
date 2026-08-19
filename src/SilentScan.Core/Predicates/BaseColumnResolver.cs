using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A (table, column) key compares its two parts <see cref="StringComparison.OrdinalIgnoreCase"/>,
/// matching every other identifier comparison in Catalog/Lineage. Promoted out of
/// <c>TypedPredicateExtractor</c> to its own public top-level type (2026-08 audit) - a bare
/// <c>HashSet&lt;(string, string)&gt;</c>/<c>Dictionary&lt;(string, string), ...&gt;</c> silently
/// takes the ordinal default comparer, and several callers build such a collection keyed by
/// <see cref="Lineage.ColumnProvenance.BaseColumn.TableQualifiedName"/> (the FROM-clause source
/// spelling) then probe it with <see cref="Catalog.CatalogTable.QualifiedName"/> (the DDL
/// spelling) or a call-site's own spelling of a callee name - a reference spelled with different
/// casing (<c>FROM DBO.ORDERS</c> against <c>CREATE TABLE dbo.Orders</c>) silently missed every
/// one of these lookups, which made a scanner treat a bound leading column as unreferenced (a
/// false violation) or drop OUTPUT-parameter propagation across a differently-cased call site.
/// ValueTuple element names are compile-time only, so this same comparer applies to any
/// <c>(string, string)</c> pair regardless of what its own element names are called.
/// </summary>
public sealed class TableColumnKeyComparer : IEqualityComparer<(string Table, string Column)>
{
    public static readonly TableColumnKeyComparer Instance = new();

    public bool Equals((string Table, string Column) x, (string Table, string Column) y) =>
        string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.Column, y.Column, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string Table, string Column) obj) =>
        HashCode.Combine(obj.Table.ToUpperInvariant(), obj.Column.ToUpperInvariant());
}

/// <summary>
/// Shared base-table-only, depth-0 column resolution used by several standalone whole-statement
/// scanners (docs/detection-checklist.md "Engineering debt" - real duplication found via a
/// SonarQube scan, not the Flatten-family sweep earlier). <see cref="ResolveBaseColumn"/>/
/// <see cref="ResolveBothSides"/> were byte-identical across <c>IndexCoverageScanner</c>,
/// <c>CompositeIndexLeadingColumnScanner</c>, and (the single-side form)
/// <c>PartialCompositeForeignKeyJoinScanner</c>; <see cref="ColumnReferenceCollector"/> was
/// byte-identical across the first two and <c>IndexHintScanner</c>, whose own doc comment already
/// called out the duplication by name without anyone extracting it. <c>ledger</c> is always null
/// for every one of these callers: the whole-statement scanners this serves run alongside
/// NonSargablePredicateScanner/TypedPredicateExtractor, which already report full coverage over
/// the same files, so an unresolved reference here would just be duplicate ledger noise.
/// </summary>
internal static class BaseColumnResolver
{
    public static (string Table, string Column)? ResolveBaseColumn(
        ScalarExpression expression, string sourcePath,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
    {
        if (expression is not ColumnReferenceExpression columnRef)
        {
            return null;
        }

        var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
        return provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn
            ? (baseColumn.TableQualifiedName, baseColumn.ColumnName)
            : null;
    }

    public static IEnumerable<(string Table, string Column)> ResolveBothSides(
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

    /// <summary>Collects every base-column reference reachable anywhere under a fragment, OR branches included - deliberately liberal, since every caller uses this set only to suppress a finding, never to trigger one.</summary>
    public sealed class ColumnReferenceCollector(
        string sourcePath,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        HashSet<(string Table, string Column)> sink) : TSqlFragmentVisitor
    {
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            // A wildcard reference (bare * in SELECT *, or COUNT(*)'s own single argument) has no
            // MultiPartIdentifier at all - ResolveColumnReference assumes a real column name is
            // present and crashes on this shape, oracle-found against real corpus text (a COUNT(*)
            // nested inside a scalar subquery's own WHERE clause). Nothing to resolve here
            // regardless - a wildcard is never a specific column reference.
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
