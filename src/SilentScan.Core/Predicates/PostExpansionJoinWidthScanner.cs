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

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<PostExpansionJoinWidthFinding> Findings { get; } = [];

        protected override void OnQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, Action continueDescent)
        {
            InspectFromClause(node.FromClause);
            continueDescent();
        }

        private void InspectFromClause(FromClause? fromClause)
        {
            if (fromClause is null)
            {
                return;
            }

            var (_, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, CurrentCteRelations(), procScope: null);
            if (ordered.Count == 0)
            {
                return;
            }

            var written = ordered.Count;
            var expandedBaseTables = new HashSet<string>(catalog.IdentifierComparer);
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
