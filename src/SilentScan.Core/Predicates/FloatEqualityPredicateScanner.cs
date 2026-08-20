using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "float/real as an
/// equality-predicate target" - see <see cref="FloatEqualityFinding"/> for the full scope/
/// precision story, including why this is a standalone type/scanner rather than folded into
/// <see cref="TypedPredicateExtractor"/>'s type-conversion-verdict machinery.
///
/// Resolves through <see cref="Lineage.FromScopeResolver"/>'s real per-statement scope chain
/// (Phase 1.5 "one binder") rather than a direct-base-table-only shortcut: a float/real predicate
/// reached through a view/derived table is still left unanalyzed (<see cref="BaseColumnResolver"/>
/// only ever resolves a real, depth-0 base column, never guesses through a view layer), but a
/// CTE-shadowed reference now resolves against the CTE's real underlying column instead of being
/// declined wholesale or - the bug this scanner's own prior file-wide CTE-name-only awareness
/// could not distinguish - mismatched against an unrelated same-named real table.
/// </summary>
public static class FloatEqualityPredicateScanner
{
    public static IReadOnlyList<FloatEqualityFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
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

        // Real per-statement CTE scope (Phase 1.5 "one binder") - a QuerySpecification has no
        // direct access to its enclosing SelectStatement's WithCtesAndXmlNamespaces, so this is
        // captured on the way down and consulted from ExplicitVisit(QuerySpecification), matching
        // ConstrainedColumnStatementVisitor's own precedent. UpdateStatement/DeleteStatement are
        // themselves top-level, so they resolve their own WITH clause directly, no stack needed.
        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> cteScopeStack = new();

        public List<FloatEqualityFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScopeStack.Push(CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null));
            base.ExplicitVisit(node);
            cteScopeStack.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var cteRelations = cteScopeStack.Count > 0 ? cteScopeStack.Peek() : EmptyResolvedViews;
            var scopeChain = ScopeChainOf(FromScopeResolver.Resolve(node.FromClause, ResolutionContext(cteRelations)));

            // node.WhereClause.SearchCondition is null for a positioned "WHERE CURRENT OF
            // @cursor" - a WhereClause with no boolean search condition at all, not a normal
            // filter predicate - so this checks the condition itself, not just the clause.
            if (node.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(node.FromClause?.TableReferences, scopeChain);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            var scopeChain = ScopeChainOf(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations)));

            // See ExplicitVisit(QuerySpecification)'s own comment - a positioned "WHERE CURRENT
            // OF @cursor" carries a null SearchCondition.
            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain);

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            var scopeChain = ScopeChainOf(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations)));

            // See ExplicitVisit(QuerySpecification)'s own comment - a positioned "WHERE CURRENT
            // OF @cursor" carries a null SearchCondition.
            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain);

            base.ExplicitVisit(node);
        }

        private FromScopeResolver.ResolutionContext ResolutionContext(IReadOnlyDictionary<string, ResolvedRelation> cteRelations) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, cteRelations, ProcScope: null);

        private static List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> ScopeChainOf(
            (IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered) resolved) => [resolved];

        /// <summary>
        /// A JOIN's own ON clause is a filter position exactly like WHERE - inspected here, against
        /// the same resolved scope chain already built for the whole FROM clause, rather than via a
        /// separate <see cref="TSqlFragmentVisitor.ExplicitVisit(QualifiedJoin)"/> override (which
        /// would need its own copy of the enclosing scope threaded through a field/stack for no
        /// benefit, since every join in one FROM clause shares the identical <paramref
        /// name="scopeChain"/> this method already has in hand).
        /// </summary>
        private void InspectJoinOnClauses(
            IList<TableReference>? tableReferences,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (tableReferences is null)
            {
                return;
            }

            foreach (var reference in tableReferences)
            {
                foreach (var join in PredicateTreeWalker.FlattenJoinNodes(reference).Where(j => j.SearchCondition is not null))
                {
                    Inspect(join.SearchCondition!, scopeChain);
                }
            }
        }

        private void Inspect(
            BooleanExpression searchCondition,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var collector = new EqualityCollector();
            searchCondition.Accept(collector);
            foreach (var comparison in collector.Comparisons)
            {
                InspectEquality(comparison, scopeChain);
            }
        }

        private void InspectEquality(
            BooleanComparisonExpression comparison,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            foreach (var side in new[] { comparison.FirstExpression, comparison.SecondExpression })
            {
                if (side is not ColumnReferenceExpression columnRef
                    || BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain) is not { } resolved
                    || resolved.Type?.Category is not (SqlTypeCategory.Real or SqlTypeCategory.Float))
                {
                    continue;
                }

                Findings.Add(new FloatEqualityFinding(
                    resolved.TableQualifiedName,
                    resolved.ColumnName,
                    resolved.Type!.ToString(),
                    sourcePath,
                    comparison.StartLine,
                    comparison.StartColumn));

                // One finding per predicate site, even when both sides resolve to a float/real
                // column (Col1 = Col2 between two such columns) - the site itself is the unit of
                // reporting, not each operand independently.
                return;
            }
        }

        /// <summary>
        /// Collects every <see cref="BooleanComparisonExpression"/> reachable from a search
        /// condition WITHOUT descending into a nested <see cref="QuerySpecification"/> - a
        /// subquery (EXISTS/IN/scalar) inside this WHERE has its own FROM scope, and is reached
        /// separately (and correctly re-scoped) by the outer visitor's own
        /// <see cref="Visitor.ExplicitVisit(QuerySpecification)"/> override when normal traversal
        /// gets there, so stopping here avoids inspecting the same comparison twice - once with
        /// this (wrong, outer) scope and once with its own (correct) scope.
        /// </summary>
        private sealed class EqualityCollector : TSqlFragmentVisitor
        {
            public List<BooleanComparisonExpression> Comparisons { get; } = [];

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (node.ComparisonType == BooleanComparisonType.Equals)
                {
                    Comparisons.Add(node);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                // Deliberately does not call base.ExplicitVisit(node) / AcceptChildren - see this
                // class's own doc comment.
            }
        }
    }
}
