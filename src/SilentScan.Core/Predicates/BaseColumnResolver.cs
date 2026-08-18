using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

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
