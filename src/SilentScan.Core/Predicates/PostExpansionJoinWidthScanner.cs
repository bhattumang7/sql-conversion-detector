using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Post-expansion join width".
/// Reuses <see cref="FromScopeResolver"/>'s own <c>Resolve</c> method (the same FROM-clause flattening every other
/// scanner in this codebase already uses) purely for its written-reference-count/qualified-name
/// output - the expansion itself comes from the already-built <see cref="ViewExpansionMap"/>.
/// </summary>
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

        /// <summary>
        /// The enclosing SELECT's own CTE scope - a QuerySpecification has no direct access to
        /// its enclosing SelectStatement's WithCtesAndXmlNamespaces. A CTE is never schema-
        /// qualified, so it always shadows a same-named real base table; resolving through the
        /// catalog instead (cteRelations always null, pre-fix) silently counted a CTE-shadowed
        /// leaf as a real, expandable base table using an unrelated real table's own expansion
        /// factor, rather than the honest "unresolved, partially unexpanded" this scanner already
        /// gives a derived table or PIVOT (2026-08 audit).
        /// </summary>
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
                    // A derived table, PIVOT/UNPIVOT, or other unresolved shape - contributes no
                    // countable base table, and the expansion below it is unknown, not zero.
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
