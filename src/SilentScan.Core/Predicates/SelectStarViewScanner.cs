using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public readonly record struct SelectStarViewCandidate(string ViewSourcePath, int StarLine, IReadOnlyList<string> FullColumns, int Depth);

public static class SelectStarViewScanner
{
    public static IReadOnlyDictionary<string, SelectStarViewCandidate> BuildCandidates(
        IReadOnlyList<ViewDefinition> views, IReadOnlyDictionary<string, ViewExpansionOrigin> viewExpansionMap, LineageCatalog lineage,
        StringComparer? identifierComparer = null)
    {
        var candidates = new Dictionary<string, SelectStarViewCandidate>(identifierComparer ?? StringComparer.OrdinalIgnoreCase);

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

        var rule = CreateRule(parseResult.SourcePath, catalog, lineage, candidates);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, lineage.AllRelations, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SelectStarViewCandidate> candidates) =>
        new(sourcePath, catalog, lineage, candidates);

    internal static IReadOnlyList<SelectStarViewFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.ViewQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ConsumerSourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.ConsumerLine)
                .ThenBy(f => f.ConsumerColumn),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SelectStarViewCandidate> candidates) : IModuleRule
    {
        public List<SelectStarViewFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker) =>
            InspectQuery(node, walker);

        private void InspectQuery(QuerySpecification node, ModuleWalker walker)
        {
            if (node.FromClause is null)
            {
                return;
            }

            var (byAlias, _) = FromScopeResolver.Resolve(node.FromClause, catalog, lineage.AllRelations, sourcePath, ledger: null, walker.CurrentCteRelations(), procScope: null);

            var wholeQueryStar = node.SelectElements.OfType<SelectStarExpression>().FirstOrDefault(s => s.Qualifier is not { Count: > 0 });

            foreach (var (alias, entry) in byAlias)
            {
                if (entry.Relation.QualifiedName is not { } qualifiedName || !candidates.TryGetValue(qualifiedName, out var candidate))
                {
                    continue;
                }

                if (wholeQueryStar is not null || AliasHasOwnStar(node, alias, catalog.IdentifierComparer))
                {

                    continue;
                }

                var selected = CollectExplicitSelectedColumns(node, alias, byAlias.Count == 1, candidate.FullColumns, catalog.IdentifierComparer);
                if (selected.Count == 0 || selected.Count >= candidate.FullColumns.Count)
                {
                    continue;
                }

                Findings.Add(new SelectStarViewFinding(
                    qualifiedName, candidate.ViewSourcePath, candidate.StarLine, candidate.FullColumns, candidate.Depth,
                    sourcePath, node.StartLine, node.StartColumn, selected));
            }
        }

        private static bool AliasHasOwnStar(QuerySpecification node, string alias, StringComparer identifierComparer) =>
            node.SelectElements.OfType<SelectStarExpression>()
                .Any(s => s.Qualifier is { Count: > 0 } q && identifierComparer.Equals(q[^1].Value, alias));

        private static List<string> CollectExplicitSelectedColumns(
            QuerySpecification node, string alias, bool isOnlySource, IReadOnlyList<string> fullColumns, StringComparer identifierComparer)
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
                    >= 2 when identifierComparer.Equals(identifiers[^2].Value, alias) => identifiers[^1].Value,
                    1 when isOnlySource => identifiers[0].Value,
                    _ => null,
                };

                if (columnName is not null && fullColumns.Contains(columnName, identifierComparer) && !selected.Contains(columnName, identifierComparer))
                {
                    selected.Add(columnName);
                }
            }

            return selected;
        }
    }
}
