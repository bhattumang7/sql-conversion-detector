using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public readonly record struct SelectStarViewCandidate(string ViewSourcePath, int StarLine, IReadOnlyList<string> FullColumns, int Depth);

public static class SelectStarViewScanner
{
    public static IReadOnlyDictionary<string, SelectStarViewCandidate> BuildCandidates(
        IReadOnlyList<ViewDefinition> views, IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap, LineageCatalog lineage)
    {
        var candidates = new Dictionary<string, SelectStarViewCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var view in views)
        {
            if (!viewExpansionMap.TryGetValue(view.QualifiedName, out var origin) || origin.Depth < 1)
            {
                continue;
            }

            var starLine = FindOutermostStarLine(view.SelectStatement.QueryExpression);
            if (starLine is null)
            {
                continue;
            }

            var relation = lineage.Find(view.QualifiedName);
            if (relation is null || relation.Columns.Count == 0)
            {
                continue;
            }

            candidates[view.QualifiedName] = new SelectStarViewCandidate(
                view.SourcePath, starLine.Value, [.. relation.Columns.Select(c => c.Name)], origin.Depth);
        }

        return candidates;
    }

    private static int? FindOutermostStarLine(QueryExpression queryExpression) =>
        queryExpression switch
        {
            QueryParenthesisExpression parenthesis => FindOutermostStarLine(parenthesis.QueryExpression),
            QuerySpecification spec => spec.SelectElements.OfType<SelectStarExpression>().Select(s => (int?)s.StartLine).FirstOrDefault(),
            _ => null,
        };

    public static IReadOnlyList<SelectStarViewFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SelectStarViewCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var visitor = new Visitor(parseResult.SourcePath, catalog, lineage, candidates);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.ViewQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ConsumerSourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.ConsumerLine)
                .ThenBy(f => f.ConsumerColumn),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SelectStarViewCandidate> candidates) : TSqlFragmentVisitor
    {
        public List<SelectStarViewFinding> Findings { get; } = [];

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyCteRelations = new Dictionary<string, ResolvedRelation>();

        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> cteScopeStack = new();

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScopeStack.Push(CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, lineage.AllRelations, sourcePath, ledger: null));
            base.ExplicitVisit(node);
            cteScopeStack.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            InspectQuery(node);
            base.ExplicitVisit(node);
        }

        private void InspectQuery(QuerySpecification node)
        {
            if (node.FromClause is null)
            {
                return;
            }

            var cteRelations = cteScopeStack.Count > 0 ? cteScopeStack.Peek() : EmptyCteRelations;
            var (byAlias, _) = FromScopeResolver.Resolve(node.FromClause, catalog, lineage.AllRelations, sourcePath, ledger: null, cteRelations, procScope: null);

            var wholeQueryStar = node.SelectElements.OfType<SelectStarExpression>().FirstOrDefault(s => s.Qualifier is not { Count: > 0 });

            foreach (var (alias, entry) in byAlias)
            {
                if (entry.Relation.QualifiedName is not { } qualifiedName || !candidates.TryGetValue(qualifiedName, out var candidate))
                {
                    continue;
                }

                if (wholeQueryStar is not null || AliasHasOwnStar(node, alias))
                {

                    continue;
                }

                var selected = CollectExplicitSelectedColumns(node, alias, byAlias.Count == 1, candidate.FullColumns);
                if (selected.Count == 0 || selected.Count >= candidate.FullColumns.Count)
                {
                    continue;
                }

                Findings.Add(new SelectStarViewFinding(
                    qualifiedName, candidate.ViewSourcePath, candidate.StarLine, candidate.FullColumns, candidate.Depth,
                    sourcePath, node.StartLine, node.StartColumn, selected));
            }
        }

        private static bool AliasHasOwnStar(QuerySpecification node, string alias) =>
            node.SelectElements.OfType<SelectStarExpression>()
                .Any(s => s.Qualifier is { Count: > 0 } q && string.Equals(q[^1].Value, alias, StringComparison.OrdinalIgnoreCase));

        private static List<string> CollectExplicitSelectedColumns(QuerySpecification node, string alias, bool isOnlySource, IReadOnlyList<string> fullColumns)
        {
            var selected = new List<string>();

            foreach (var element in node.SelectElements.OfType<SelectScalarExpression>())
            {
                if (element.Expression is not ColumnReferenceExpression columnRef)
                {
                    continue;
                }

                var identifiers = columnRef.MultiPartIdentifier.Identifiers;
                string? columnName = identifiers.Count switch
                {
                    >= 2 when string.Equals(identifiers[^2].Value, alias, StringComparison.OrdinalIgnoreCase) => identifiers[^1].Value,
                    1 when isOnlySource => identifiers[0].Value,
                    _ => null,
                };

                if (columnName is not null && fullColumns.Contains(columnName, StringComparer.OrdinalIgnoreCase) && !selected.Contains(columnName, StringComparer.OrdinalIgnoreCase))
                {
                    selected.Add(columnName);
                }
            }

            return selected;
        }
    }
}
