using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "<c>TRY_CAST</c> in a
/// non-persisted computed column used in a predicate" - see
/// <see cref="TryCastComputedColumnPredicateFinding"/> for the full precision story and oracle
/// evidence. Two-step, the same "build a small side-map once, then scan per-file against it" shape
/// <see cref="SelectStarViewScanner"/> already uses: <see cref="BuildCandidates"/> finds every
/// non-persisted, <c>TRY_CAST</c>-defined computed column once from the catalog; <see cref="Scan"/>
/// then walks each file's own AST for a genuine filter-context reference to one of them.
/// </summary>
public static partial class TryCastComputedColumnPredicateScanner
{
    [GeneratedRegex(@"\bTRY_CAST\b", RegexOptions.IgnoreCase)]
    private static partial Regex TryCastPattern();

    public readonly record struct Candidate(string DefinitionText, string SourcePath, int Line);

    /// <summary>
    /// Every (table, column) whose own computed-column definition text uses <c>TRY_CAST</c> and
    /// whose catalog <see cref="CatalogColumn.IsPersisted"/> is false - the latter is a defensive,
    /// belt-and-suspenders re-check (the oracle already proves a genuine <c>TRY_CAST</c> computed
    /// column can never legally be PERSISTED), not a scope narrowing.
    /// </summary>
    public static IReadOnlyDictionary<(string TableQualifiedName, string ColumnName), Candidate> BuildCandidates(DatabaseCatalog catalog)
    {
        var candidates = new Dictionary<(string, string), Candidate>();

        foreach (var expression in catalog.SchemaExpressions)
        {
            if (expression.Kind != SchemaDependencyKind.ComputedColumn || expression.ColumnName is not { } columnName)
            {
                continue;
            }

            if (!TryCastPattern().IsMatch(expression.DefinitionText))
            {
                continue;
            }

            var column = catalog.Find(expression.TableQualifiedName)?.FindColumn(columnName);
            if (column is not { IsComputed: true, IsPersisted: false })
            {
                continue;
            }

            candidates[(expression.TableQualifiedName, columnName)] =
                new Candidate(expression.DefinitionText, expression.SourcePath, expression.Line);
        }

        return candidates;
    }

    public static IReadOnlyList<TryCastComputedColumnPredicateFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<(string TableQualifiedName, string ColumnName), Candidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var visitor = new Visitor(parseResult.SourcePath, catalog, candidates);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal)
                .ThenBy(f => f.PredicateSourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.PredicateLine)
                .ThenBy(f => f.PredicateColumn),
        ];
    }

    /// <summary>
    /// Deliberately base-table-only (mirrors <see cref="CatchAllPredicateScanner"/>'s own
    /// documented v1 scope limit): no CTE/view/temp-table scoping, each statement's own FROM
    /// clause resolved fresh via <see cref="FromScopeResolver"/> with an empty resolved-views map.
    /// </summary>
    private sealed class Visitor(
        string sourcePath, DatabaseCatalog catalog,
        IReadOnlyDictionary<(string TableQualifiedName, string ColumnName), Candidate> candidates) : TSqlFragmentVisitor
    {
        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

        public List<TryCastComputedColumnPredicateFinding> Findings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.FromClause is not null)
            {
                var (byAlias, ordered) = FromScopeResolver.Resolve(node.FromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations: null, procScope: null);
                var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };

                InspectSearchCondition(node.WhereClause?.SearchCondition, scopeChain);
                InspectSearchCondition(node.HavingClause?.SearchCondition, scopeChain);
                foreach (var tableReference in node.FromClause.TableReferences)
                {
                    InspectJoins(tableReference, scopeChain);
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext());
            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };
            InspectSearchCondition(spec.WhereClause?.SearchCondition, scopeChain);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext());
            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };
            InspectSearchCondition(spec.WhereClause?.SearchCondition, scopeChain);
            base.ExplicitVisit(node);
        }

        private FromScopeResolver.ResolutionContext ResolutionContext() =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteRelations: null, ProcScope: null);

        private void InspectJoins(TableReference tableReference, List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            switch (tableReference)
            {
                case QualifiedJoin join:
                    InspectJoins(join.FirstTableReference, scopeChain);
                    InspectJoins(join.SecondTableReference, scopeChain);
                    InspectSearchCondition(join.SearchCondition, scopeChain);
                    break;
                case UnqualifiedJoin unqualified:
                    InspectJoins(unqualified.FirstTableReference, scopeChain);
                    InspectJoins(unqualified.SecondTableReference, scopeChain);
                    break;
                default:
                    break;
            }
        }

        private void InspectSearchCondition(
            BooleanExpression? searchCondition,
            List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (searchCondition is null)
            {
                return;
            }

            var collector = new ColumnReferenceCollector();
            searchCondition.Accept(collector);

            foreach (var columnRef in collector.References)
            {
                var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
                if (provenance is not ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
                {
                    continue;
                }

                if (!candidates.TryGetValue((baseColumn.TableQualifiedName, baseColumn.ColumnName), out var candidate))
                {
                    continue;
                }

                Findings.Add(new TryCastComputedColumnPredicateFinding(
                    baseColumn.TableQualifiedName, baseColumn.ColumnName, candidate.DefinitionText, candidate.SourcePath, candidate.Line,
                    sourcePath, columnRef.StartLine, columnRef.StartColumn));
            }
        }

        /// <summary>Collects every column reference reachable within a search condition subtree, regardless of how deeply it's wrapped (a further function/CAST wrap around the already-non-seekable computed column doesn't change this finding at all) - but never descends into a nested subquery's own scope (<see cref="ExplicitVisit(QuerySpecification)"/> visits that separately, under its own resolved FROM scope).</summary>
        private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
        {
            public List<ColumnReferenceExpression> References { get; } = [];

            public override void ExplicitVisit(ColumnReferenceExpression node) => References.Add(node);

            public override void ExplicitVisit(QuerySpecification node)
            {
                // Stop here - a nested subquery (EXISTS/IN/scalar) has its own FROM scope, which
                // this base-table-only v1 walk does not attempt to resolve inline.
            }

            public override void ExplicitVisit(ScalarSubquery node)
            {
                // Same reasoning as QuerySpecification above.
            }
        }
    }
}
