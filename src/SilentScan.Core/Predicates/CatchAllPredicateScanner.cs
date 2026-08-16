using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Catch-all / kitchen-sink predicates" - a standalone
/// scanner, not folded into <see cref="TypedPredicateExtractor"/>'s per-comparison walk: the
/// catch-all shape spans a whole <c>BooleanBinaryExpression{Or}</c> combining two SIBLING boolean
/// expressions (an equality and an IS NULL check), a genuinely different traversal shape than
/// <c>TryAddFinding</c>'s one-comparison-at-a-time walk - the same reasoning <see
/// cref="PartialCompositeForeignKeyJoinScanner"/> already documents for why it's a separate
/// scanner rather than bolted onto the existing predicate walk.
///
/// Deliberately base-table-only, like <see cref="PartialCompositeForeignKeyJoinScanner"/>: column
/// resolution goes through <see cref="FromScopeResolver"/> with no CTE/view/temp-table scoping
/// (empty resolved-views map, null ledger/CTE map/proc scope) - a known v1 scope limit, not a
/// silently-missed case. Formal-parameter/RECOMPILE-guard tracking is this scanner's own copy of
/// the identical logic <see cref="TypedPredicateExtractor"/> uses for the same purpose - a
/// separate visitor over the same AST, so it cannot share that class's private state.
/// </summary>
public static class CatchAllPredicateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<CatchAllPredicateFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
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
        public List<CatchAllPredicateFinding> Findings { get; } = [];

        private readonly HashSet<string> _formalParameterNames = new(StringComparer.OrdinalIgnoreCase);

        private bool _procedureHasWithRecompile;

        private bool _statementHasOptionRecompile;

        private bool HasActiveRecompileGuard => _procedureHasWithRecompile || _statementHasOptionRecompile;

        public override void ExplicitVisit(CreateProcedureStatement node) =>
            VisitProcedureOrFunctionBody(node.Parameters, node.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile), node);

        public override void ExplicitVisit(AlterProcedureStatement node) =>
            VisitProcedureOrFunctionBody(node.Parameters, node.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile), node);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) =>
            VisitProcedureOrFunctionBody(node.Parameters, node.Options.Any(o => o.OptionKind == ProcedureOptionKind.Recompile), node);

        public override void ExplicitVisit(CreateFunctionStatement node) => VisitProcedureOrFunctionBody(node.Parameters, hasWithRecompile: false, node);

        public override void ExplicitVisit(AlterFunctionStatement node) => VisitProcedureOrFunctionBody(node.Parameters, hasWithRecompile: false, node);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitProcedureOrFunctionBody(node.Parameters, hasWithRecompile: false, node);

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitProcedureOrFunctionBody([], hasWithRecompile: false, node);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitProcedureOrFunctionBody([], hasWithRecompile: false, node);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitProcedureOrFunctionBody([], hasWithRecompile: false, node);

        private void VisitProcedureOrFunctionBody(IList<ProcedureParameter> parameters, bool hasWithRecompile, TSqlFragment node)
        {
            var previousFormalParameterNames = new HashSet<string>(_formalParameterNames, StringComparer.OrdinalIgnoreCase);
            var previousProcedureHasWithRecompile = _procedureHasWithRecompile;

            _formalParameterNames.Clear();
            foreach (var parameter in parameters)
            {
                _formalParameterNames.Add(parameter.VariableName.Value);
            }

            _procedureHasWithRecompile = hasWithRecompile;
            node.AcceptChildren(this);

            _formalParameterNames.Clear();
            foreach (var name in previousFormalParameterNames)
            {
                _formalParameterNames.Add(name);
            }

            _procedureHasWithRecompile = previousProcedureHasWithRecompile;
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            var previous = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previous;
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (!HasActiveRecompileGuard)
            {
                var (byAlias, ordered) = FromScopeResolver.Resolve(node.FromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations: null, procScope: null);
                InspectSearchCondition(node.WhereClause?.SearchCondition, byAlias, ordered);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var previous = BeginStatementOptimizerHints(node.OptimizerHints);
            var spec = node.UpdateSpecification;
            if (!HasActiveRecompileGuard)
            {
                var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext());
                InspectSearchCondition(spec.WhereClause?.SearchCondition, byAlias, ordered);
            }

            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previous;
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var previous = BeginStatementOptimizerHints(node.OptimizerHints);
            var spec = node.DeleteSpecification;
            if (!HasActiveRecompileGuard)
            {
                var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext());
                InspectSearchCondition(spec.WhereClause?.SearchCondition, byAlias, ordered);
            }

            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previous;
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var previous = BeginStatementOptimizerHints(node.OptimizerHints);
            base.ExplicitVisit(node);
            _statementHasOptionRecompile = previous;

            // MERGE's own ON clause is a genuine catch-all site too, in principle, but its scope
            // resolution (FromScopeResolver.ResolveForMerge) and raw SearchCondition shape differ
            // enough from every other statement kind that covering it precisely needs its own
            // dedicated work - out of v1 scope, a known limitation, not silently missed.
        }

        private FromScopeResolver.ResolutionContext ResolutionContext() =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteRelations: null, ProcScope: null);

        private bool BeginStatementOptimizerHints(IList<OptimizerHint> hints)
        {
            var previous = _statementHasOptionRecompile;
            _statementHasOptionRecompile = hints.Any(h => h.HintKind == OptimizerHintKind.Recompile);
            return previous;
        }

        private void InspectSearchCondition(
            BooleanExpression? searchCondition,
            IReadOnlyDictionary<string, ScopeEntry> byAlias,
            IReadOnlyList<ScopeEntry> ordered)
        {
            if (searchCondition is null)
            {
                return;
            }

            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };

            foreach (var orClause in FlattenOr(searchCondition))
            {
                InspectOrClause(orClause, scopeChain);
            }
        }

        /// <summary>Flattens every top-level OR-connected fragment reachable without crossing an AND - <c>(A OR B) AND (C OR D)</c> yields two independent 2-fragment groups, never one flat 4-fragment group (mixing them would let a fragment from one AND-branch pair with an unrelated fragment from another).</summary>
        private static IEnumerable<IReadOnlyList<BooleanExpression>> FlattenOr(BooleanExpression expression)
        {
            switch (expression)
            {
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                    foreach (var group in FlattenOr(and.FirstExpression))
                    {
                        yield return group;
                    }

                    foreach (var group in FlattenOr(and.SecondExpression))
                    {
                        yield return group;
                    }

                    break;

                case BooleanParenthesisExpression paren:
                    foreach (var group in FlattenOr(paren.Expression))
                    {
                        yield return group;
                    }

                    break;

                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or }:
                    yield return [.. FlattenOrLeaves(expression)];
                    break;

                default:
                    break;
            }
        }

        private static IEnumerable<BooleanExpression> FlattenOrLeaves(BooleanExpression expression)
        {
            switch (expression)
            {
                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } or_:
                    foreach (var leaf in FlattenOrLeaves(or_.FirstExpression))
                    {
                        yield return leaf;
                    }

                    foreach (var leaf in FlattenOrLeaves(or_.SecondExpression))
                    {
                        yield return leaf;
                    }

                    break;

                case BooleanParenthesisExpression paren:
                    foreach (var leaf in FlattenOrLeaves(paren.Expression))
                    {
                        yield return leaf;
                    }

                    break;

                default:
                    yield return expression;
                    break;
            }
        }

        private void InspectOrClause(
            IReadOnlyList<BooleanExpression> orLeaves,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            // Match every (equality, IS NULL) pair sharing the same parameter name - a chain of
            // several independent catch-all clauses ORed together (`Col = @p OR @p IS NULL OR
            // Col2 = @q OR @q IS NULL`) yields one finding per matched pair, not one for the
            // whole chain.
            var isNullVariables = orLeaves
                .OfType<BooleanIsNullExpression>()
                .Where(n => !n.IsNot)
                .Select(n => n.Expression as VariableReference)
                .Where(v => v is not null)
                .Select(v => v!.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var equality in orLeaves.OfType<BooleanComparisonExpression>().Where(c => c.ComparisonType == BooleanComparisonType.Equals))
            {
                TryMatchCatchAllPair(equality.FirstExpression, equality.SecondExpression, isNullVariables, scopeChain, equality);
                TryMatchCatchAllPair(equality.SecondExpression, equality.FirstExpression, isNullVariables, scopeChain, equality);
            }
        }

        private void TryMatchCatchAllPair(
            ScalarExpression columnSide, ScalarExpression variableSide, HashSet<string> isNullVariables,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            TSqlFragment node)
        {
            if (columnSide is not ColumnReferenceExpression columnRef
                || variableSide is not VariableReference variableRef
                || !isNullVariables.Contains(variableRef.Name)
                || !_formalParameterNames.Contains(variableRef.Name))
            {
                return;
            }

            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
            if (provenance is not ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
            {
                return;
            }

            var indexed = catalog.Find(baseColumn.TableQualifiedName)?.IsIndexedColumn(baseColumn.ColumnName) ?? false;

            Findings.Add(new CatchAllPredicateFinding(
                baseColumn.TableQualifiedName, baseColumn.ColumnName, indexed, variableRef.Name,
                sourcePath, node.StartLine, node.StartColumn));
        }
    }
}
