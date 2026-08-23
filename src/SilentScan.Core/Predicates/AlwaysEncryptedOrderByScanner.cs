using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

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
        private readonly CteScopeTracker cteScope = new(sourcePath, catalog);

        public List<AlwaysEncryptedOrderByFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScope.PushForSelect(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            cteScope.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.OrderByClause is { OrderByElements.Count: > 0 } orderByClause)
            {
                var resolutionContext = PredicateVisitorSupport.ResolutionContext(cteScope.Current, sourcePath, catalog);
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
