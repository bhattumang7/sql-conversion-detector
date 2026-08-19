using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>A candidate view: its own source location, its SELECT * line, and its already-*-expanded full output column set (from <see cref="LineageCatalog.AllRelations"/>).</summary>
public readonly record struct SelectStarViewCandidate(string ViewSourcePath, int StarLine, IReadOnlyList<string> FullColumns, int Depth);

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "SELECT * inside a view or
/// inline TVF". Two-step, matching <see cref="ViewExpansionMap"/> + <see cref="PostExpansionJoinWidthScanner"/>'s
/// own "build a small side-map once, then scan per-file against it" shape: <see cref="BuildCandidates"/>
/// finds every view/TVF whose own outermost SELECT is a bare/qualified <c>*</c> and whose <see
/// cref="ViewExpansionOrigin.Depth"/> is ≥ 1, then <see cref="Scan"/> walks every query site
/// corpus-wide for a consumer that explicitly selects a strict, named subset of that view's full
/// column set.
/// </summary>
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

    /// <summary>Only the view's own OUTERMOST query specification's own SELECT list is inspected - a * nested only inside an inner derived-table subquery does not itself qualify the view, and a top-level UNION declines rather than guessing which branch's star matters.</summary>
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
                .ThenBy(f => f.ConsumerLine),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, LineageCatalog lineage, IReadOnlyDictionary<string, SelectStarViewCandidate> candidates) : TSqlFragmentVisitor
    {
        public List<SelectStarViewFinding> Findings { get; } = [];

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyCteRelations = new Dictionary<string, ResolvedRelation>();

        /// <summary>
        /// The enclosing SELECT's own CTE scope - a QuerySpecification has no direct access to
        /// its enclosing SelectStatement's WithCtesAndXmlNamespaces. A CTE is never schema-
        /// qualified, so it always shadows a same-named real base table/view; resolving without
        /// it (cteRelations always null, pre-fix) could match a CTE-shadowed reference against an
        /// unrelated real view sharing its name (2026-08 audit).
        /// </summary>
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
                    // The consumer itself does SELECT * (bare, or alias.*) - never narrows
                    // anything by construction, so it can never be the finding this rule targets.
                    continue;
                }

                var selected = CollectExplicitSelectedColumns(node, alias, byAlias.Count == 1, candidate.FullColumns);
                if (selected.Count == 0 || selected.Count >= candidate.FullColumns.Count)
                {
                    continue;
                }

                Findings.Add(new SelectStarViewFinding(
                    qualifiedName, candidate.ViewSourcePath, candidate.StarLine, candidate.FullColumns, candidate.Depth,
                    sourcePath, node.StartLine, selected));
            }
        }

        private static bool AliasHasOwnStar(QuerySpecification node, string alias) =>
            node.SelectElements.OfType<SelectStarExpression>()
                .Any(s => s.Qualifier is { Count: > 0 } q && string.Equals(q[^1].Value, alias, StringComparison.OrdinalIgnoreCase));

        /// <summary>Only a bare ColumnReferenceExpression explicitly qualified with this alias, or unqualified when this is the query's ONLY FROM source (no ambiguity to resolve), counts as "selected" - any other shape is declined, not guessed.</summary>
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
