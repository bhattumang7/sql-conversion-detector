using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// See <see cref="AlwaysEncryptedOrderByFinding"/> for the full scope/precision story. Resolves
/// through <see cref="Lineage.FromScopeResolver"/>'s real per-statement scope chain (matching
/// <see cref="FloatEqualityPredicateScanner"/>'s own precedent) rather than a direct-base-table-only
/// shortcut, so a CTE-shadowed reference resolves against its real underlying column.
/// </summary>
public static class AlwaysEncryptedOrderByScanner
{
    public static IReadOnlyList<AlwaysEncryptedOrderByFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

        // Real per-statement CTE scope (matching FloatEqualityPredicateScanner's own stack -
        // a QuerySpecification has no direct access to its enclosing SelectStatement's
        // WithCtesAndXmlNamespaces).
        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> cteScopeStack = new();

        public List<AlwaysEncryptedOrderByFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScopeStack.Push(CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null));
            base.ExplicitVisit(node);
            cteScopeStack.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.OrderByClause is { OrderByElements.Count: > 0 } orderByClause)
            {
                var cteRelations = cteScopeStack.Count > 0 ? cteScopeStack.Peek() : EmptyResolvedViews;
                var resolutionContext = new FromScopeResolver.ResolutionContext(catalog, EmptyResolvedViews, sourcePath, Ledger: null, cteRelations, ProcScope: null);
                var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)>
                {
                    FromScopeResolver.Resolve(node.FromClause, resolutionContext),
                };

                foreach (var element in orderByClause.OrderByElements)
                {
                    Inspect(element, scopeChain);
                }
            }

            base.ExplicitVisit(node);
        }

        private void Inspect(
            ExpressionWithSortOrder element,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (element.Expression is not ColumnReferenceExpression columnRef
                || BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain) is not { } resolved
                || catalog.Find(resolved.TableQualifiedName)?.FindColumn(resolved.ColumnName) is not { } catalogColumn
                || catalogColumn.EncryptionType is not (Catalog.ColumnEncryptionType.Deterministic or Catalog.ColumnEncryptionType.Randomized))
            {
                return;
            }

            Findings.Add(new AlwaysEncryptedOrderByFinding(
                resolved.TableQualifiedName,
                resolved.ColumnName,
                catalogColumn.EncryptionType.ToString(),
                sourcePath,
                element.StartLine,
                element.StartColumn));
        }
    }
}
