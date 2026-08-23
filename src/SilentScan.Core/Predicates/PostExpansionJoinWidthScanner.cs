using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class PostExpansionJoinWidthScanner
{
    public const int MinimumGap = 3;

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<PostExpansionJoinWidthFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog, viewExpansionMap);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderByDescending(f => f.ExpandedCount - f.WrittenCount)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap) : TSqlFragmentVisitor
    {
        public List<PostExpansionJoinWidthFinding> Findings { get; } = [];

        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> cteScopeStack = new();

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScopeStack.Push(CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null));
            base.ExplicitVisit(node);
            cteScopeStack.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            InspectFromClause(node.FromClause);
            base.ExplicitVisit(node);
        }

        private void InspectFromClause(FromClause? fromClause)
        {
            if (fromClause is null)
            {
                return;
            }

            var cteRelations = cteScopeStack.Count > 0 ? cteScopeStack.Peek() : EmptyResolvedViews;
            var (_, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations, procScope: null);
            if (ordered.Count == 0)
            {
                return;
            }

            var written = ordered.Count;
            var expandedBaseTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inflatingSources = new List<string>();
            var partiallyUnexpanded = false;

            foreach (var entry in ordered)
            {
                var qualifiedName = entry.Relation.QualifiedName;
                if (qualifiedName is null)
                {

                    partiallyUnexpanded = true;
                    continue;
                }

                if (viewExpansionMap.TryGetValue(qualifiedName, out var origin))
                {
                    expandedBaseTables.UnionWith(origin.BaseTables);
                    partiallyUnexpanded |= origin.PartiallyUnexpanded;
                    if (origin.BaseTables.Count > 1)
                    {
                        inflatingSources.Add(qualifiedName);
                    }
                }
                else
                {
                    expandedBaseTables.Add(qualifiedName);
                }
            }

            var expanded = expandedBaseTables.Count;
            if (expanded - written < MinimumGap)
            {
                return;
            }

            Findings.Add(new PostExpansionJoinWidthFinding(
                sourcePath, written, expanded, [.. expandedBaseTables.OrderBy(t => t, StringComparer.Ordinal)],
                inflatingSources, partiallyUnexpanded, sourcePath, fromClause.StartLine, fromClause.StartColumn));
        }
    }
}
